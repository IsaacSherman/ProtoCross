import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ProtoLangApi } from '../../../src/extension';

// Inside a real VS Code, with the fixture workspace open: what a person installing the extension would
// see. The launch rules and trust are tested against the real server in test/server, where the
// environment and the trust state can be controlled; VS Code's test instance runs with trust disabled.

const extensionId = 'isaacsherman.protolang';

function workspaceFile(name: string): string {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(folder !== undefined, 'the tests need the fixture workspace open');
  return path.join(folder.uri.fsPath, name);
}

async function until<T>(read: () => T | undefined, describe: string, patienceMs = 60_000): Promise<T> {
  const deadline = Date.now() + patienceMs;
  for (;;) {
    const value = read();
    if (value !== undefined) {
      return value;
    }
    if (Date.now() > deadline) {
      throw new Error(`Never saw ${describe}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

async function api(): Promise<ProtoLangApi> {
  const extension = vscode.extensions.getExtension<ProtoLangApi>(extensionId);
  assert.ok(extension !== undefined, `${extensionId} is not installed in the test instance`);
  return extension.activate();
}

async function running(protolang: ProtoLangApi): Promise<number | undefined> {
  const state = await until(
    () => (protolang.controller.state.kind === 'running' ? protolang.controller.state : undefined),
    'the language server running',
  );
  return state.processId;
}

function problems(uri: vscode.Uri): vscode.Diagnostic[] {
  return vscode.languages.getDiagnostics(uri);
}

describe('the extension, installed', () => {
  let source: vscode.Uri;

  before(async () => {
    source = vscode.Uri.file(workspaceFile('source.protolang'));
    const document = await vscode.workspace.openTextDocument(source);
    await vscode.window.showTextDocument(document);
  });

  it('registers .protolang as ProtoLang', async () => {
    const document = await vscode.workspace.openTextDocument(source);
    assert.equal(document.languageId, 'protolang');
  });

  it('starts the server with no configuration and publishes live diagnostics', async () => {
    await running(await api());

    const diagnostics = await until(
      () => (problems(source).some((problem) => problem.severity === vscode.DiagnosticSeverity.Error) ? problems(source) : undefined),
      'an error for the field the schema lacks',
    );

    assert.ok(diagnostics.some((problem) => /width/.test(problem.message)), 'the error should be about width');
  });

  it("does not tell the server about the extension's own settings", async () => {
    await until(() => (problems(source).length > 0 ? true : undefined), 'diagnostics');

    assert.ok(
      problems(source).every((problem) => String(typeof problem.code === 'object' ? problem.code.value : problem.code) !== 'PL2102'),
      `the extension's settings were reported as unknown: ${problems(source).map((problem) => problem.message).join(' | ')}`,
    );
  });

  it('refreshes diagnostics when an imported schema is saved, with no edit to the file', async () => {
    fs.writeFileSync(workspaceFile('shape.proto'), 'syntax = "proto3";\n\nmessage Shape {\n  int64 height = 1;\n  int64 width = 2;\n}\n');

    await until(
      () => (problems(source).every((problem) => problem.severity !== vscode.DiagnosticSeverity.Error) ? true : undefined),
      'the error to clear once width was added to the schema',
    );
  });

  it('starts the server outside every workspace folder', async () => {
    const protolang = await api();
    await running(protolang);

    const directory = protolang.controller.lastLaunch?.workingDirectory;
    assert.ok(directory !== undefined);
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
      const relative = path.relative(folder.uri.fsPath, directory);
      assert.ok(relative.startsWith('..') || path.isAbsolute(relative), `'${directory}' is inside '${folder.uri.fsPath}'`);
    }
  });

  it("reports the extension's version and this session's restarts in the status report", async () => {
    const protolang = await api();
    await running(protolang);

    const report = await protolang.collectStatus();

    assert.equal(report.fromServer, true);
    const version = vscode.extensions.getExtension(extensionId)?.packageJSON.version as string;
    assert.match(report.markdown, new RegExp(`\\| extension \\| ${version.replace(/\./g, '\\.')} \\|`));
    assert.match(report.markdown, /\| restarts this session \| \d+ \|/);
    assert.ok(report.privacy.length > 0 && report.markdown.includes(report.privacy));
  });

  it('recovers from the server crashing, without restarting the editor', async () => {
    const protolang = await api();
    const before = await running(protolang);
    const restarts = protolang.controller.restarts;
    assert.ok(before !== undefined, 'the server process must be known to be killed');

    process.kill(before);

    await until(
      () =>
        protolang.controller.state.kind === 'running' && protolang.controller.state.processId !== before ? true : undefined,
      'a new server process',
    );
    assert.equal(protolang.controller.restarts, restarts + 1);

    const report = await protolang.collectStatus();
    assert.match(report.markdown, new RegExp(`\\| restarts this session \\| ${restarts + 1} \\|`));
  });

  it('still produces a report when the server cannot start, saying why', async () => {
    const protolang = await api();
    const missing = path.join(path.dirname(workspaceFile('source.protolang')), 'no-such-server.dll');
    const configuration = vscode.workspace.getConfiguration('protolang');

    await configuration.update('server.path', missing, vscode.ConfigurationTarget.Global);

    try {
      await until(() => (protolang.controller.state.kind === 'failed' ? true : undefined), 'the launch to fail');

      const report = await protolang.collectStatus();

      assert.equal(report.fromServer, false);
      assert.match(report.markdown, /failed to start/);
      assert.ok(report.markdown.includes(missing), 'the report should name the server it could not find');
      assert.ok(report.markdown.includes(report.privacy));
    } finally {
      await configuration.update('server.path', undefined, vscode.ConfigurationTarget.Global);
    }

    await running(protolang);
  });
});
