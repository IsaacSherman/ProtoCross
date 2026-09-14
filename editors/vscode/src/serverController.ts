import { execFile, spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';
import {
  CloseAction,
  ErrorAction,
  LanguageClient,
  RevealOutputChannelOn,
  State,
  type LanguageClientOptions,
} from 'vscode-languageclient/node';
import {
  dotnetCandidates,
  hasSupportedRuntime,
  initializationOptions,
  isAbsoluteLocation,
  isAssembly,
  logLevels,
  minimumDotnetMajor,
  parseRuntimes,
  sanitizeEnvironment,
  serverCommand,
  type LogLevel,
} from './launch';
import { section, withoutExtensionSettings } from './settings';

/** Where the language server stands, as far as this extension can tell. */
export type ServerState =
  | { readonly kind: 'disabled' }
  | { readonly kind: 'stopped' }
  | { readonly kind: 'starting' }
  | { readonly kind: 'running'; readonly processId: number | undefined }
  | { readonly kind: 'failed'; readonly summary: string; readonly detail: string };

/** What the last launch was made of, for the status report and the log. */
export interface LaunchFacts {
  readonly server: string;
  readonly dotnet: string | undefined;
  readonly runtimes: readonly string[];
  readonly workingDirectory: string;
  readonly removedPathEntries: readonly string[];
  readonly removedVariables: readonly string[];
  readonly logLevel: LogLevel;
}

/** What the user can be offered when the server cannot start. */
type Remedy = 'installDotnet' | 'chooseServer' | 'none';

/** A reason the server cannot be started that the user can do something about. */
class LaunchProblem extends Error {
  constructor(
    readonly summary: string,
    readonly detail: string,
    readonly remedy: Remedy,
  ) {
    super(`${summary} ${detail}`);
  }
}

/** Where .NET can be downloaded, for a machine that has none new enough. */
const dotnetDownloadPage = 'https://dotnet.microsoft.com/download';

/** How many crashes within {@link crashWindowMs} are recovered from before giving up. */
const crashesTolerated = 4;
const crashWindowMs = 3 * 60 * 1000;

/**
 * Starts, restarts and stops the language server, and knows why it is not running when it is not.
 *
 * Every launch is serialized through {@link queue}. A setting change, a crash and a command can all
 * arrive together, and two starts interleaving would leave two servers and one client, or a client
 * pointed at a process that another start has already killed.
 */
export class ServerController implements vscode.Disposable {
  private client: LanguageClient | undefined;
  private process: ChildProcess | undefined;
  private queue: Promise<void> = Promise.resolve();
  private current: ServerState = { kind: 'stopped' };
  private launched = false;
  private restartCount = 0;
  private crashes: number[] = [];
  private facts: LaunchFacts | undefined;
  private readonly remediesOffered = new Set<Remedy>();
  private readonly stateChanged = new vscode.EventEmitter<ServerState>();
  private readonly subscriptions: vscode.Disposable[] = [];

  /** Fires whenever {@link state} changes. */
  readonly onDidChangeState = this.stateChanged.event;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly log: vscode.LogOutputChannel,
  ) {
    this.subscriptions.push(
      this.stateChanged,
      vscode.workspace.onDidGrantWorkspaceTrust(() => this.reportTrustGranted()),
      vscode.workspace.onDidChangeConfiguration((change) => {
        if (launchSettings.some((setting) => change.affectsConfiguration(`${section}.${setting}`))) {
          void this.restart('the settings that start the server changed');
        }
      }),
    );
  }

  /** Where the server stands now. */
  get state(): ServerState {
    return this.current;
  }

  /** How many times this session the server has been started again after a first launch. */
  get restarts(): number {
    return this.restartCount;
  }

  /** What the last launch was made of, or undefined when nothing has been launched. */
  get lastLaunch(): LaunchFacts | undefined {
    return this.facts;
  }

  /** The client talking to the running server, or undefined when none is running. */
  get languageClient(): LanguageClient | undefined {
    return this.current.kind === 'running' ? this.client : undefined;
  }

  /** Starts the server, unless the user turned it off. */
  start(): Promise<void> {
    return this.enqueue(() => this.launch());
  }

  /** Stops the server and starts it again, counting it as a restart if one had been launched. */
  restart(reason: string): Promise<void> {
    return this.enqueue(async () => {
      this.log.info(`Restarting the language server: ${reason}.`);
      await this.shutDown();
      await this.launch();
    });
  }

  /** Stops the server. */
  stop(): Promise<void> {
    return this.enqueue(async () => {
      await this.shutDown();
      this.transition({ kind: 'stopped' });
    });
  }

  dispose(): void {
    void this.stop();
    for (const subscription of this.subscriptions) {
      subscription.dispose();
    }
  }

  private enqueue(operation: () => Promise<void>): Promise<void> {
    this.queue = this.queue.then(operation, operation);
    return this.queue;
  }

  private transition(state: ServerState): void {
    this.current = state;
    this.stateChanged.fire(state);
  }

  // ------------------------------------------------------------------ launching

  private async launch(): Promise<void> {
    if (!vscode.workspace.getConfiguration(section).get<boolean>('server.enabled', true)) {
      this.log.info('The language server is turned off (protolang.server.enabled); colouring still works.');
      this.transition({ kind: 'disabled' });
      return;
    }

    if (this.launched) {
      this.restartCount++;
    }
    this.launched = true;
    this.transition({ kind: 'starting' });

    try {
      const { facts, env } = await this.plan();
      this.facts = facts;
      this.client = this.createClient(facts, env);
      await this.client.start();
      this.transition({ kind: 'running', processId: this.process?.pid });
    } catch (error) {
      await this.fail(error);
    }
  }

  /**
   * Everything a launch needs, settled before anything is started, so that a reason not to start is
   * found and reported rather than discovered as a process that exits.
   *
   * The environment is returned beside the facts rather than inside them: the facts go into a status
   * report somebody pastes into an issue, and an environment holds whatever secrets the user's shell
   * does.
   */
  private async plan(): Promise<{ facts: LaunchFacts; env: Record<string, string> }> {
    const configuration = vscode.workspace.getConfiguration(section);
    const { env, removedPathEntries, removedVariables } = sanitizeEnvironment(process.env, process.platform);

    // Extension-owned, so a path inside a workspace folder is never the directory anything relative
    // resolves against. Created if missing, since a first run has never written to it.
    const workingDirectory = this.context.globalStorageUri.fsPath;
    await fs.promises.mkdir(workingDirectory, { recursive: true });

    const server = this.serverPath();
    const dotnet = isAssembly(server) ? this.findDotnet(env) : undefined;
    const runtimes = dotnet === undefined ? [] : await this.runtimesOf(dotnet, env, workingDirectory);

    if (dotnet !== undefined && !hasSupportedRuntime(runtimes)) {
      throw new LaunchProblem(
        `ProtoLang's language server needs .NET ${minimumDotnetMajor} or newer.`,
        runtimes.length === 0
          ? `'${dotnet}' reports no .NET runtime installed.`
          : `'${dotnet}' has ${runtimes.join(', ')}.`,
        'installDotnet',
      );
    }

    const level = configuration.get<string>('logLevel', 'info');
    const logLevel = (logLevels as readonly string[]).includes(level) ? (level as LogLevel) : 'info';

    if (removedPathEntries.length > 0) {
      this.log.info(`Relative PATH entries are not passed to the server: ${removedPathEntries.join(', ')}.`);
    }
    if (removedVariables.length > 0) {
      this.log.info(`Relative values of ${removedVariables.join(', ')} are not passed to the server.`);
    }

    return {
      facts: { server, dotnet, runtimes, workingDirectory, removedPathEntries, removedVariables, logLevel },
      env,
    };
  }

  /**
   * The server this extension ships, or the one `protolang.server.path` names.
   *
   * Only the user's own value of that setting is consulted. It is machine-scoped, so a workspace cannot
   * set it anyway, and it is restricted in the manifest -- but the rule #45 states is that the extension
   * does not use a workspace value of an executable setting before trust, and reading the user value
   * alone is that rule written down rather than inherited from two other mechanisms.
   */
  private serverPath(): string {
    const configured = userValue('server.path');

    if (configured === undefined) {
      const bundled = this.context.asAbsolutePath(path.join('server', 'protolang-server.dll'));
      if (!fs.existsSync(bundled)) {
        throw new LaunchProblem(
          'The ProtoLang language server is missing from this extension.',
          `Nothing is at '${bundled}'. Reinstall the extension, or set protolang.server.path to a server you built.`,
          'chooseServer',
        );
      }
      return bundled;
    }

    if (!isAbsoluteLocation(configured, process.platform)) {
      throw new LaunchProblem(
        'protolang.server.path must be a full path.',
        `'${configured}' is relative, and would be looked for somewhere nobody chose.`,
        'chooseServer',
      );
    }

    if (!fs.existsSync(configured)) {
      throw new LaunchProblem(
        'The language server named by protolang.server.path does not exist.',
        `Nothing is at '${configured}'.`,
        'chooseServer',
      );
    }

    return configured;
  }

  /** The `dotnet` to run the server with, by absolute path, or a problem saying where it looked. */
  private findDotnet(env: Record<string, string>): string {
    const configured = userValue('dotnetPath');

    if (configured !== undefined) {
      if (isAbsoluteLocation(configured, process.platform) && isFile(configured)) {
        return configured;
      }
      throw new LaunchProblem(
        'protolang.dotnetPath does not name a dotnet executable.',
        `'${configured}' is ${isAbsoluteLocation(configured, process.platform) ? 'not a file' : 'not a full path'}.`,
        'installDotnet',
      );
    }

    const candidates = dotnetCandidates(env, process.platform, os.homedir());
    const found = candidates.find(isFile);

    if (found === undefined) {
      throw new LaunchProblem(
        `ProtoLang's live features need .NET ${minimumDotnetMajor} or newer, and no dotnet was found.`,
        `Looked in DOTNET_ROOT, on PATH, and in ${candidates.length} usual install locations. ` +
          'Syntax colouring works without it.',
        'installDotnet',
      );
    }

    return found;
  }

  /** The runtimes `dotnet` reports. Run with the same environment and directory the server gets. */
  private runtimesOf(dotnet: string, env: Record<string, string>, cwd: string): Promise<string[]> {
    return new Promise((resolve, reject) => {
      execFile(dotnet, ['--list-runtimes'], { env, cwd, timeout: 15_000, windowsHide: true }, (error, stdout) => {
        if (error !== null) {
          reject(
            new LaunchProblem(
              `'${dotnet}' could not be asked which runtimes it has.`,
              error.message,
              'installDotnet',
            ),
          );
          return;
        }
        resolve(parseRuntimes(stdout));
      });
    });
  }

  private createClient(facts: LaunchFacts, env: Record<string, string>): LanguageClient {
    const command = serverCommand(facts.server, facts.dotnet, facts.logLevel);

    const clientOptions: LanguageClientOptions = {
      documentSelector: [
        { scheme: 'file', language: 'protolang' },
        { scheme: 'untitled', language: 'protolang' },
      ],
      outputChannel: this.log,
      revealOutputChannelOn: RevealOutputChannelOn.Never,
      synchronize: { configurationSection: section },
      // A function, so each start reports trust as it stands at that moment rather than at activation.
      initializationOptions: () =>
        initializationOptions(
          this.context.extension.id,
          this.context.extension.packageJSON.version as string,
          vscode.workspace.isTrusted,
          facts.removedPathEntries,
        ),
      middleware: {
        workspace: {
          configuration: async (params, token, next) => {
            const answers = await next(params, token);
            return Array.isArray(answers) ? answers.map(withoutExtensionSettings) : answers;
          },
        },
      },
      errorHandler: {
        error: (_error, _message, count) => ({
          action: (count ?? 0) <= 3 ? ErrorAction.Continue : ErrorAction.Shutdown,
          handled: true,
        }),
        closed: () => this.closed(),
      },
    };

    const client = new LanguageClient(
      'protolang',
      'ProtoLang Language Server',
      () => this.spawnServer(command.command, command.args, facts.workingDirectory, env),
      clientOptions,
    );

    // The client restarts a crashed server by itself, without this controller's launch, so its own
    // notion of running is what says the restart worked.
    client.onDidChangeState(({ newState }) => {
      if (newState === State.Running && this.client === client) {
        this.transition({ kind: 'running', processId: this.process?.pid });
      }
    });

    return client;
  }

  /**
   * Starts the server process: by absolute path, with no shell, in the extension's own directory, with
   * the sanitized environment. Waits for the process to actually exist, so a missing executable is a
   * failure to start rather than a connection that closes a moment later for no stated reason.
   */
  private spawnServer(
    command: string,
    args: string[],
    workingDirectory: string,
    env: Record<string, string>,
  ): Promise<ChildProcess> {
    this.log.info(`Starting '${command}' ${args.map((arg) => `'${arg}'`).join(' ')} in '${workingDirectory}'.`);

    const child = spawn(command, args, { cwd: workingDirectory, env, shell: false, windowsHide: true, stdio: 'pipe' });

    return new Promise((resolve, reject) => {
      child.once('spawn', () => {
        this.process = child;
        resolve(child);
      });
      child.once('error', (error) =>
        reject(new LaunchProblem('The language server could not be started.', `'${command}': ${error.message}`, 'chooseServer')),
      );
    });
  }

  /**
   * The connection closed without anybody asking it to: the server crashed or exited. Recovered from a
   * few times, and then left stopped with a reason, because a server that dies on start will otherwise
   * be restarted for as long as the window is open.
   */
  private closed(): { action: CloseAction; handled: boolean } {
    const now = Date.now();
    this.crashes = [...this.crashes.filter((when) => now - when < crashWindowMs), now];

    if (this.crashes.length <= crashesTolerated) {
      this.restartCount++;
      this.log.warn(`The language server stopped unexpectedly; starting it again (${this.crashes.length} in three minutes).`);
      this.transition({ kind: 'starting' });
      return { action: CloseAction.Restart, handled: true };
    }

    const summary = `The language server stopped ${this.crashes.length} times in three minutes, so it has not been started again.`;
    this.transition({ kind: 'failed', summary, detail: 'The log says what it was doing.' });
    void vscode.window
      .showErrorMessage(`ProtoLang: ${summary}`, 'Restart', 'Show Log')
      .then((choice) => this.act(choice));
    return { action: CloseAction.DoNotRestart, handled: true };
  }

  private async fail(error: unknown): Promise<void> {
    const client = this.client;
    this.client = undefined;
    this.process = undefined;

    if (client !== undefined) {
      await client.dispose(2000).catch(() => undefined);
    }

    const problem =
      error instanceof LaunchProblem
        ? error
        : new LaunchProblem('The language server did not start.', error instanceof Error ? error.message : String(error), 'none');

    this.log.error(`${problem.summary} ${problem.detail}`);
    this.transition({ kind: 'failed', summary: problem.summary, detail: problem.detail });
    this.offer(problem);
  }

  /**
   * Tells the user once per session per kind of remedy, so a restart loop or a settings edit does not
   * reopen the same notification each time.
   */
  private offer(problem: LaunchProblem): void {
    if (this.remediesOffered.has(problem.remedy)) {
      return;
    }
    this.remediesOffered.add(problem.remedy);

    const actions =
      problem.remedy === 'installDotnet'
        ? ['Install .NET', 'Use Without the Server', 'Show Log']
        : problem.remedy === 'chooseServer'
          ? ['Open Settings', 'Show Log']
          : ['Show Log'];

    void vscode.window
      .showWarningMessage(`ProtoLang: ${problem.summary} ${problem.detail}`, ...actions)
      .then((choice) => this.act(choice));
  }

  private async act(choice: string | undefined): Promise<void> {
    switch (choice) {
      case 'Install .NET':
        await vscode.env.openExternal(vscode.Uri.parse(dotnetDownloadPage));
        break;
      case 'Use Without the Server':
        await vscode.workspace.getConfiguration(section).update('server.enabled', false, vscode.ConfigurationTarget.Global);
        break;
      case 'Open Settings':
        await vscode.commands.executeCommand('workbench.action.openSettings', `@ext:${this.context.extension.id}`);
        break;
      case 'Show Log':
        this.log.show(true);
        break;
      case 'Restart':
        this.crashes = [];
        await this.restart('asked to from the notification');
        break;
    }
  }

  private async shutDown(): Promise<void> {
    const client = this.client;
    const child = this.process;
    this.client = undefined;
    this.process = undefined;

    if (client === undefined) {
      return;
    }

    try {
      await client.stop(2000);
    } catch (error) {
      this.log.warn(`The language server did not stop when asked: ${error instanceof Error ? error.message : String(error)}`);
      child?.kill();
    }

    await client.dispose().catch(() => undefined);
  }

  /** The workspace was trusted: say so, so a withheld protoc path takes effect without a restart. */
  private reportTrustGranted(): void {
    void this.languageClient
      ?.sendNotification('protolang/didChangeWorkspaceTrust', { trusted: true })
      .catch((error: unknown) => this.log.warn(`Could not tell the server the workspace is trusted: ${String(error)}`));
  }
}

/** The settings that decide how the server is started, so changing one restarts it. */
const launchSettings = ['server', 'dotnetPath', 'logLevel'] as const;

/** A setting's value as the user wrote it in their own settings, ignoring the workspace's. */
function userValue(key: string): string | undefined {
  const value = vscode.workspace.getConfiguration(section).inspect<string>(key)?.globalValue?.trim();
  return value === undefined || value.length === 0 ? undefined : value;
}

function isFile(candidate: string): boolean {
  try {
    return fs.statSync(candidate).isFile();
  } catch {
    return false;
  }
}
