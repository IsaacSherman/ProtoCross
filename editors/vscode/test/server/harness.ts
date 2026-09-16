import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  createMessageConnection,
  StreamMessageReader,
  StreamMessageWriter,
  type MessageConnection,
} from 'vscode-jsonrpc/node';
import { dotnetCandidates, sanitizeEnvironment } from '../../src/launch';

/**
 * Starts the server this extension ships -- the staged `server/protolang-server.dll`, through a real
 * dotnet, as a real process -- and speaks LSP to it without VS Code.
 *
 * What the extension's launch decides is tested here against the process it produces, because the
 * properties #45 asks for are about what that process can find: an unit test of the environment says
 * a relative entry was removed, and only a server started with it can say that removing it mattered.
 */

/** Where the extension lives, from the bundle this runs as (out/test/server). */
export const extensionRoot = path.resolve(__dirname, '..', '..', '..');

export const stagedServer = path.join(extensionRoot, 'server', 'protolang-server.dll');

export const pathSeparator = process.platform === 'win32' ? ';' : ':';

/** A fresh directory of this test's own. */
export function temporaryDirectory(label: string): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), `protolang-${label}-`));
}

/** The dotnet a user of this machine would get, by absolute path. */
export function findDotnet(): string {
  const { env } = sanitizeEnvironment(process.env, process.platform);
  const found = dotnetCandidates(env, process.platform, os.homedir()).find((candidate) => {
    try {
      return fs.statSync(candidate).isFile();
    } catch {
      return false;
    }
  });

  if (found === undefined) {
    throw new Error('These tests start the real server, and no dotnet was found to run it with.');
  }

  return found;
}

export interface Launch {
  readonly env: Record<string, string>;
  readonly cwd: string;
  readonly initializationOptions: unknown;
  /** The folder the client says it opened, if any. */
  readonly folder?: string;
  /** What `workspace/configuration` is answered with, for every scope asked about. */
  readonly settings?: Record<string, unknown>;
}

export interface StatusFact {
  readonly label: string;
  readonly value: string;
  readonly source?: string;
}

export interface StatusSection {
  readonly title: string;
  readonly facts: StatusFact[];
  readonly note?: string;
}

export interface Diagnostics {
  readonly uri: string;
  readonly diagnostics: { message: string; code?: string; severity?: number }[];
}

/** One running server, and what it has said. */
export class Server {
  readonly published: Diagnostics[] = [];
  readonly shown: string[] = [];

  private constructor(
    private readonly process: ChildProcess,
    readonly connection: MessageConnection,
    private readonly stderr: string[],
  ) {}

  static async start(launch: Launch): Promise<Server> {
    if (!fs.existsSync(stagedServer)) {
      throw new Error(`No server is staged at '${stagedServer}'. Run 'npm run stage' first.`);
    }

    const child = spawn(findDotnet(), [stagedServer, '--log-level=info'], {
      cwd: launch.cwd,
      env: launch.env,
      shell: false,
      windowsHide: true,
      stdio: 'pipe',
    });

    const stderr: string[] = [];
    child.stderr.on('data', (chunk: Buffer) => stderr.push(chunk.toString()));

    const connection = createMessageConnection(new StreamMessageReader(child.stdout), new StreamMessageWriter(child.stdin));
    const server = new Server(child, connection, stderr);

    connection.onRequest('workspace/configuration', (params: { items: unknown[] }) =>
      params.items.map(() => launch.settings ?? {}),
    );
    connection.onRequest('client/registerCapability', () => null);
    connection.onNotification('textDocument/publishDiagnostics', (params: Diagnostics) => {
      server.published.push(params);
    });
    connection.onNotification('window/showMessage', (params: { message: string }) => {
      server.shown.push(params.message);
    });
    connection.listen();

    await connection.sendRequest('initialize', {
      processId: process.pid,
      rootUri: null,
      capabilities: { workspace: { configuration: true, workspaceFolders: true } },
      workspaceFolders:
        launch.folder === undefined ? [] : [{ uri: pathToFileURL(launch.folder).href, name: path.basename(launch.folder) }],
      initializationOptions: launch.initializationOptions,
    });
    await connection.sendNotification('initialized', {});

    return server;
  }

  /** The server's status report, as sections. */
  async status(): Promise<StatusSection[]> {
    const result = await this.connection.sendRequest<{ sections: StatusSection[] }>('protolang/status', {});
    return result.sections;
  }

  /** The protoc the server would run, as its status report states it. */
  async protoc(): Promise<StatusSection> {
    const section = (await this.status()).find((candidate) => candidate.title === 'protoc');
    if (section === undefined) {
      throw new Error('The status report has no protoc section.');
    }
    return section;
  }

  async open(file: string): Promise<string> {
    const uri = pathToFileURL(file).href;
    await this.connection.sendNotification('textDocument/didOpen', {
      textDocument: { uri, languageId: 'protolang', version: 1, text: fs.readFileSync(file, 'utf8') },
    });
    return uri;
  }

  /** Waits for something to become true, failing with what the server wrote if it never does. */
  async until(condition: () => boolean, describe: string, patienceMs = 30_000): Promise<void> {
    const deadline = Date.now() + patienceMs;
    while (!condition()) {
      if (Date.now() > deadline) {
        throw new Error(`Never saw ${describe}. The server wrote:\n${this.stderr.join('')}`);
      }
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }

  async stop(): Promise<void> {
    try {
      await this.connection.sendRequest('shutdown');
      await this.connection.sendNotification('exit');
    } catch {
      // Already gone, which is the state being asked for.
    }

    await new Promise<void>((resolve) => {
      if (this.process.exitCode !== null) {
        resolve();
        return;
      }
      const timer = setTimeout(() => {
        this.process.kill();
        resolve();
      }, 5000);
      this.process.once('exit', () => {
        clearTimeout(timer);
        resolve();
      });
    });

    this.connection.dispose();
  }
}

/**
 * A program standing where protoc could be named, which leaves a file behind if anything starts it.
 * Asserted on the file rather than on anything the server says, because a server that ran it and threw
 * the answer away would say exactly the right things.
 */
export function tattletale(directory: string, marker: string): string {
  if (process.platform === 'win32') {
    const script = path.join(directory, 'protoc.cmd');
    fs.writeFileSync(script, `@echo off\r\necho ran> "${marker}"\r\nexit /b 1\r\n`);
    return script;
  }

  const script = path.join(directory, 'protoc');
  fs.writeFileSync(script, `#!/bin/sh\necho ran > '${marker}'\nexit 1\n`, { mode: 0o755 });
  return script;
}

/** A file named as protoc is on this platform, which is all the locator's probes check for. */
export function fileNamedProtoc(directory: string): string {
  const file = path.join(directory, process.platform === 'win32' ? 'protoc.exe' : 'protoc');
  fs.writeFileSync(file, '');
  return file;
}
