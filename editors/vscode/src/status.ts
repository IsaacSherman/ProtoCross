import * as os from 'node:os';
import * as vscode from 'vscode';
import type { ServerController, ServerState } from './serverController';
import { privacyNote } from './settings';
import { renderReport, type Section, type StatusReport } from './statusReport';

/** The scheme the report is shown under, so the copy button appears on it and nowhere else. */
export const statusScheme = 'protocross-status';

/** How long the server gets to answer before the extension reports that it did not. */
const patienceMs = 15_000;

/** The server's answer, as far as this command reads it. */
interface ServerStatusResult {
  readonly markdown: string;
  readonly privacy: string;
}

/**
 * The status command: asks the server for its report, writes one itself when the server cannot give
 * one, shows it, and copies it only after the note about file paths has been read.
 *
 * #58 asks for the report to exist when the server failed to start or is not answering, which is the
 * one time the server cannot help. So the extension's own report is not a degraded copy of the
 * server's; it is the part only the extension can know -- whether a process exists at all, and why not.
 */
export class StatusCommand implements vscode.TextDocumentContentProvider, vscode.Disposable {
  private latest: StatusReport | undefined;
  private readonly changed = new vscode.EventEmitter<vscode.Uri>();
  private readonly registration: vscode.Disposable;

  readonly onDidChange = this.changed.event;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly controller: ServerController,
  ) {
    this.registration = vscode.workspace.registerTextDocumentContentProvider(statusScheme, this);
  }

  provideTextDocumentContent(): string {
    return this.latest?.markdown ?? '';
  }

  dispose(): void {
    this.registration.dispose();
    this.changed.dispose();
  }

  /** Collects a report and opens it beside the editor. */
  async show(): Promise<StatusReport> {
    const report = await this.collect();
    this.latest = report;

    const uri = vscode.Uri.from({ scheme: statusScheme, path: '/ProtoCross Status.md' });
    this.changed.fire(uri);

    const document = await vscode.workspace.openTextDocument(uri);
    await vscode.languages.setTextDocumentLanguage(document, 'markdown');
    await vscode.window.showTextDocument(document, { preview: true, viewColumn: vscode.ViewColumn.Beside });

    void vscode.window
      .showInformationMessage('ProtoCross: the language server status report is open.', 'Copy Report')
      .then((choice) => (choice === 'Copy Report' ? this.copy() : undefined));

    return report;
  }

  /**
   * Copies the whole report in one action, having shown the privacy note first. Collects one if none
   * has been shown, so the command works from the palette on its own.
   */
  async copy(): Promise<boolean> {
    const report = this.latest ?? (await this.collect());
    this.latest = report;

    const choice = await vscode.window.showWarningMessage(report.privacy, { modal: true }, 'Copy Report');
    if (choice !== 'Copy Report') {
      return false;
    }

    await vscode.env.clipboard.writeText(report.markdown);
    return true;
  }

  /** The server's report when it answers in time, and the extension's own when it does not. */
  async collect(): Promise<StatusReport> {
    const client = this.controller.languageClient;

    if (client === undefined) {
      return this.ownReport(this.controller.state, undefined);
    }

    const cancellation = new vscode.CancellationTokenSource();
    const timer = setTimeout(() => cancellation.cancel(), patienceMs);

    try {
      const result = await client.sendRequest<ServerStatusResult>(
        'protocross/status',
        {
          textDocument: activeProtoCrossDocument(),
          client: {
            extensionName: this.context.extension.id,
            extensionVersion: this.version,
            restarts: this.controller.restarts,
          },
        },
        cancellation.token,
      );

      return { markdown: result.markdown, privacy: result.privacy, fromServer: true };
    } catch (error) {
      const why = cancellation.token.isCancellationRequested
        ? `it did not answer within ${patienceMs / 1000} seconds`
        : `it answered with an error: ${error instanceof Error ? error.message : String(error)}`;
      return this.ownReport(this.controller.state, why);
    } finally {
      clearTimeout(timer);
      cancellation.dispose();
    }
  }

  private get version(): string {
    return this.context.extension.packageJSON.version as string;
  }

  /** What the extension knows about a server that could not report on itself. */
  private ownReport(state: ServerState, unanswered: string | undefined): StatusReport {
    const launch = this.controller.lastLaunch;

    const sections: Section[] = [
      {
        title: 'Versions',
        facts: [
          { label: 'extension', value: this.version, source: this.context.extension.id },
          { label: 'editor', value: vscode.version, source: vscode.env.appName },
          { label: 'platform', value: `${process.platform} ${process.arch}`, source: os.release() },
        ],
      },
      {
        title: 'Server',
        facts: [
          { label: 'state', value: describeState(state, unanswered) },
          { label: 'restarts this session', value: String(this.controller.restarts), source: 'counted by the extension' },
          { label: 'server', value: launch?.server ?? '(not launched)' },
          { label: 'dotnet', value: launch?.dotnet ?? '(not used)' },
          { label: 'runtimes', value: launch?.runtimes.join(', ') || '(none reported)' },
          { label: 'working directory', value: launch?.workingDirectory ?? '(not launched)' },
          { label: 'log level', value: launch?.logLevel ?? '(not launched)' },
        ],
        note: noteFor(state, unanswered),
      },
    ];

    return { markdown: renderReport(privacyNote, sections), privacy: privacyNote, fromServer: false };
  }
}

function describeState(state: ServerState, unanswered: string | undefined): string {
  if (unanswered !== undefined) {
    return 'not answering';
  }

  switch (state.kind) {
    case 'failed':
      return 'failed to start';
    case 'disabled':
      return 'turned off';
    default:
      return state.kind;
  }
}

function noteFor(state: ServerState, unanswered: string | undefined): string {
  if (unanswered !== undefined) {
    return `**The server is running but ${unanswered}.** Its log may say what it was doing; ` +
      'ProtoCross: Restart Language Server starts a fresh one.';
  }

  switch (state.kind) {
    case 'failed':
      return `**${state.summary}** ${state.detail}`;
    case 'disabled':
      return 'protocross.server.enabled is off, so only syntax colouring is active.';
    case 'starting':
      return 'The server is still starting. Run this again in a moment for its own report.';
    default:
      return 'No server is running.';
  }
}

/** The ProtoCross document the user is looking at, if they are looking at one. */
function activeProtoCrossDocument(): { uri: string } | undefined {
  const document = vscode.window.activeTextEditor?.document;
  return document?.languageId === 'protocross' ? { uri: document.uri.toString() } : undefined;
}
