import * as vscode from 'vscode';
import { ServerController } from './serverController';
import { StatusCommand } from './status';
import type { StatusReport } from './statusReport';

/**
 * What activation hands back. Used by this extension's own end-to-end tests, which have no other way
 * to reach the process they are asserting about; nothing else should depend on it.
 */
export interface ProtoLangApi {
  readonly controller: ServerController;
  collectStatus(): Promise<StatusReport>;
}

export async function activate(context: vscode.ExtensionContext): Promise<ProtoLangApi> {
  const log = vscode.window.createOutputChannel('ProtoLang Language Server', { log: true });
  const controller = new ServerController(context, log);
  const status = new StatusCommand(context, controller);

  context.subscriptions.push(
    log,
    controller,
    status,
    vscode.commands.registerCommand('protolang.restartServer', () => controller.restart('asked to by the user')),
    vscode.commands.registerCommand('protolang.showStatus', () => status.show()),
    vscode.commands.registerCommand('protolang.copyStatus', () => status.copy()),
    vscode.commands.registerCommand('protolang.showLog', () => log.show(true)),
  );

  // Not awaited. Activation returning is what lets the grammar and the commands work, and a server that
  // takes seconds to start -- or cannot start at all -- must not hold either of them up.
  void controller.start();

  return { controller, collectStatus: () => status.collect() };
}

export function deactivate(): void {
  // Everything is in context.subscriptions, which VS Code disposes.
}
