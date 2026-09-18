import * as path from 'node:path';

/**
 * How the language server is started, decided without touching VS Code, so that every rule here can
 * be tested by a plain Node test and none of it is folklore about what the extension happens to do.
 *
 * The rules come from spec 10.4.1 and #45, and they all serve one purpose: nothing the server looks
 * up may resolve into the workspace. A bare `protoc`, a relative `PATH` entry and a relative
 * `PROTOCROSS_PROTOC` or `NUGET_PACKAGES` all resolve against the server's working directory, so a
 * server started inside a repository could find a `protoc` committed to it and run it before anybody
 * trusted that repository. Workspace trust withholds the settings a repository writes; it cannot
 * withhold a working directory.
 */

/** The oldest .NET the server runs on. It targets net10.0 and rolls forward to any newer major. */
export const minimumDotnetMajor = 10;

/** Environment variables naming a directory or file the server reads, which must not be relative. */
export const pathVariables = ['PROTOCROSS_PROTOC', 'NUGET_PACKAGES'] as const;

/** What the server is started with once the environment has been made safe. */
export interface LaunchEnvironment {
  /** The environment to start the server with. */
  readonly env: Record<string, string>;
  /**
   * The relative `PATH` entries that were removed, as written. The server is told them, so that a
   * user whose protoc lived in one can be told why it was not found. Empty entries are removed and
   * not reported: they are nearly always a stray separator rather than a place anybody put a tool.
   */
  readonly removedPathEntries: string[];
  /** The variables from {@link pathVariables} that were dropped for holding a relative path. */
  readonly removedVariables: string[];
}

/**
 * Whether `entry` names a location without reference to a working directory.
 *
 * Stricter than `path.isAbsolute` on Windows, deliberately. `\tools` is rooted but has no drive, so it
 * means a different directory depending on which drive the process started on, and `C:tools` is
 * relative to whatever directory drive C last had. Only a drive with a separator after it, or a UNC
 * path, says where it is on its own. A quoted entry is judged by what is inside the quotes, since
 * Windows accepts them in `PATH`.
 */
export function isAbsoluteLocation(entry: string, platform: NodeJS.Platform): boolean {
  const unquoted = entry.trim().replace(/^"(.*)"$/, '$1');

  if (platform === 'win32') {
    return /^[A-Za-z]:[\\/]/.test(unquoted) || /^[\\/]{2}[^\\/]/.test(unquoted);
  }

  return unquoted.startsWith('/');
}

/** The name `PATH` is stored under. Windows spells it however the machine was set up, often `Path`. */
function pathKeyOf(env: Record<string, string>, platform: NodeJS.Platform): string {
  if (platform !== 'win32') {
    return 'PATH';
  }

  return Object.keys(env).find((key) => key.toUpperCase() === 'PATH') ?? 'PATH';
}

/**
 * The environment the server is started with: this one, with every relative `PATH` entry and every
 * relative {@link pathVariables} value removed, and everything else -- absolute `PATH` entries
 * included -- kept exactly as it was, so a `protoc` or `dotnet` the user installed goes on being
 * found.
 */
export function sanitizeEnvironment(
  source: NodeJS.ProcessEnv,
  platform: NodeJS.Platform,
): LaunchEnvironment {
  const env: Record<string, string> = {};

  for (const [key, value] of Object.entries(source)) {
    if (value !== undefined) {
      env[key] = value;
    }
  }

  const removedPathEntries: string[] = [];
  const pathKey = pathKeyOf(env, platform);
  const separator = platform === 'win32' ? ';' : ':';

  if (env[pathKey] !== undefined) {
    const kept: string[] = [];

    for (const entry of env[pathKey].split(separator)) {
      if (isAbsoluteLocation(entry, platform)) {
        kept.push(entry);
      } else if (entry.trim().length > 0) {
        removedPathEntries.push(entry);
      }
    }

    env[pathKey] = kept.join(separator);
  }

  const removedVariables: string[] = [];

  for (const name of pathVariables) {
    const key = Object.keys(env).find((candidate) =>
      platform === 'win32' ? candidate.toUpperCase() === name : candidate === name,
    );

    if (key !== undefined && env[key].trim().length > 0 && !isAbsoluteLocation(env[key], platform)) {
      removedVariables.push(name);
      delete env[key];
    }
  }

  return { env, removedPathEntries, removedVariables };
}

/** What the executable is called on this platform. */
export function executableName(name: string, platform: NodeJS.Platform): string {
  return platform === 'win32' ? `${name}.exe` : name;
}

/**
 * Every place `dotnet` may be, in the order they are tried: `DOTNET_ROOT`, each absolute `PATH`
 * entry, then where the official installers and the common package managers put it.
 *
 * Only absolute candidates are produced, so the answer is never a name the operating system would go
 * and search for -- #45 asks for the server to be started by absolute path, and the runtime that
 * starts it is the first half of that. The environment given should already be sanitized.
 */
export function dotnetCandidates(
  env: Record<string, string>,
  platform: NodeJS.Platform,
  home: string,
): string[] {
  const join = platform === 'win32' ? path.win32.join : path.posix.join;
  const dotnet = executableName('dotnet', platform);
  const lookup = (name: string): string | undefined =>
    platform === 'win32'
      ? Object.entries(env).find(([key]) => key.toUpperCase() === name.toUpperCase())?.[1]
      : env[name];

  const directories: string[] = [];

  const root = lookup('DOTNET_ROOT');
  if (root !== undefined && isAbsoluteLocation(root, platform)) {
    directories.push(root);
  }

  const separator = platform === 'win32' ? ';' : ':';
  for (const entry of (lookup('PATH') ?? '').split(separator)) {
    if (isAbsoluteLocation(entry, platform)) {
      directories.push(entry.trim().replace(/^"(.*)"$/, '$1'));
    }
  }

  if (platform === 'win32') {
    for (const variable of ['ProgramFiles', 'ProgramW6432', 'LOCALAPPDATA']) {
      const base = lookup(variable);
      if (base !== undefined && isAbsoluteLocation(base, platform)) {
        directories.push(variable === 'LOCALAPPDATA' ? join(base, 'Microsoft', 'dotnet') : join(base, 'dotnet'));
      }
    }
  } else if (platform === 'darwin') {
    directories.push('/usr/local/share/dotnet', '/opt/homebrew/opt/dotnet/libexec', '/usr/local/opt/dotnet/libexec');
  } else {
    directories.push('/usr/share/dotnet', '/usr/lib/dotnet', '/usr/lib64/dotnet', '/snap/dotnet-sdk/current');
  }

  if (isAbsoluteLocation(home, platform)) {
    directories.push(join(home, '.dotnet'));
  }

  return [...new Set(directories.map((directory) => join(directory, dotnet)))];
}

/** The versions of the .NET runtime `dotnet --list-runtimes` reports, in the order it listed them. */
export function parseRuntimes(listing: string): string[] {
  const versions: string[] = [];

  for (const line of listing.split(/\r?\n/)) {
    const match = /^Microsoft\.NETCore\.App\s+(\S+)/.exec(line.trim());
    if (match !== null) {
      versions.push(match[1]);
    }
  }

  return versions;
}

/**
 * Whether any of `versions` can run the server, which rolls forward to any newer major.
 *
 * A prerelease of any major is not one of them. Roll-forward from a release version considers only
 * release versions unless DOTNET_ROLL_FORWARD_TO_PRERELEASE says otherwise, so the preview is not
 * the runtime the server would be given -- whether it is a preview of the minimum major or of one
 * above it. Saying so before the launch is what turns a process that starts and immediately exits,
 * for a reason the host wrote to a stream nobody is reading, into the offer to install .NET that
 * every other too-old machine gets.
 */
export function hasSupportedRuntime(versions: readonly string[]): boolean {
  return versions.some((version) => {
    if (version.includes('-')) {
      return false;
    }

    const major = Number.parseInt(version.split('.')[0], 10);
    return Number.isFinite(major) && major >= minimumDotnetMajor;
  });
}

/** The log levels the server accepts, and the setting offers. */
export const logLevels = ['error', 'warning', 'info', 'trace'] as const;
export type LogLevel = (typeof logLevels)[number];

/** How to start a server: which executable, with which arguments. */
export interface ServerCommand {
  readonly command: string;
  readonly args: string[];
}

/**
 * The command that starts `server`: through `dotnet` when it is an assembly, directly when it is an
 * executable. Both paths are expected to be absolute already; this does not search for either.
 */
export function serverCommand(server: string, dotnet: string | undefined, logLevel: LogLevel): ServerCommand {
  const logArgument = `--log-level=${logLevel}`;

  if (isAssembly(server)) {
    if (dotnet === undefined) {
      throw new Error(`'${server}' is an assembly and needs dotnet to run it.`);
    }

    return { command: dotnet, args: [server, logArgument] };
  }

  return { command: server, args: [logArgument] };
}

/** What the server is told about this client at `initialize`. */
export interface InitializationOptions {
  readonly extensionName: string;
  readonly extensionVersion: string;
  readonly workspaceTrusted: boolean;
  readonly removedPathEntries: readonly string[];
}

/**
 * What the server is told at `initialize`, in one place so a test starting the real server sends what
 * the extension sends.
 *
 * `workspaceTrusted` is always stated: the server treats a client that says nothing as trusted (spec
 * 10.4.1), so leaving it out would not be a safe failure. The extension's name and version are what
 * the status report prints for them, and `removedPathEntries` is what lets the server explain a
 * protoc that lived in one.
 */
export function initializationOptions(
  extensionName: string,
  extensionVersion: string,
  workspaceTrusted: boolean,
  removedPathEntries: readonly string[],
): InitializationOptions {
  return { extensionName, extensionVersion, workspaceTrusted, removedPathEntries };
}

/** Whether `server` is a .NET assembly, which is run by `dotnet` rather than started on its own. */
export function isAssembly(server: string): boolean {
  return server.toLowerCase().endsWith('.dll');
}
