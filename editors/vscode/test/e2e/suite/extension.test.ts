import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ProtoCrossApi } from '../../../src/extension';

// Inside a real VS Code, with the fixture workspace open: what a person installing the extension would
// see. The launch rules and trust are tested against the real server in test/server, where the
// environment and the trust state can be controlled; VS Code's test instance runs with trust disabled.

const extensionId = 'isaacsherman.protocross';

function workspaceFile(name: string): string {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(folder !== undefined, 'the tests need the fixture workspace open');
  return path.join(folder.uri.fsPath, name);
}

// The description is a function where what is worth saying on failure is only known by then.
async function until<T>(read: () => T | undefined, describe: string | (() => string), patienceMs = 60_000): Promise<T> {
  const deadline = Date.now() + patienceMs;
  for (;;) {
    const value = read();
    if (value !== undefined) {
      return value;
    }
    if (Date.now() > deadline) {
      throw new Error(`Never saw ${typeof describe === 'string' ? describe : describe()}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

interface Sighting {
  readonly seen: boolean;
  dispose(): void;
}

// Whether VS Code reports any change to a workspace file of this name from now on. A glob given as a
// string starts no watcher of its own: VS Code filters one stream it runs for the whole workspace, and
// the language client's watchers are filters on that same stream.
function watchFor(name: string): Sighting {
  const watcher = vscode.workspace.createFileSystemWatcher(`**/${name}`);
  let seen = false;
  const note = (): void => {
    seen = true;
  };
  const listening = [watcher.onDidCreate(note), watcher.onDidChange(note), watcher.onDidDelete(note)];

  return {
    get seen() {
      return seen;
    },
    dispose() {
      listening.forEach((listener) => listener.dispose());
      watcher.dispose();
    },
  };
}

// Waits until VS Code's workspace watcher is reporting changes. It starts on its own schedule -- on
// macOS, an FSEvents stream, which never reports what happened before it began -- so a file written in
// a session's first seconds can go unreported however correct the server is (#118). A probe no server
// watches is rewritten until a change to it arrives, which leaves whatever a test writes next as the
// one write that test makes.
async function workspaceWatcherRunning(): Promise<void> {
  const probe = watchFor('watcher-probe.txt');
  try {
    await until(() => {
      if (probe.seen) {
        return true;
      }
      fs.writeFileSync(workspaceFile('watcher-probe.txt'), `${Date.now()}\n`);
      return undefined;
    }, "VS Code's file watcher report a change in the workspace");
  } finally {
    probe.dispose();
  }
}

async function api(): Promise<ProtoCrossApi> {
  const extension = vscode.extensions.getExtension<ProtoCrossApi>(extensionId);
  assert.ok(extension !== undefined, `${extensionId} is not installed in the test instance`);
  return extension.activate();
}

async function running(protocross: ProtoCrossApi): Promise<number | undefined> {
  const state = await until(
    () => (protocross.controller.state.kind === 'running' ? protocross.controller.state : undefined),
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
    source = vscode.Uri.file(workspaceFile('source.pcross'));
    const document = await vscode.workspace.openTextDocument(source);
    await vscode.window.showTextDocument(document);
  });

  it('registers .pcross as ProtoCross', async () => {
    const document = await vscode.workspace.openTextDocument(source);
    assert.equal(document.languageId, 'protocross');
  });

  // The word pattern is what double-click selects and what a completion replaces, and VS Code compiles
  // a pattern given as a plain string with no flags at all -- which turns every \p{...} in it into a
  // literal 'p' and leaves a lexer that accepts any letter paired with an editor that finds no word.
  it('treats a name with letters from outside ASCII as one word', async () => {
    const name = 'ünïcödé_1';
    const document = await vscode.workspace.openTextDocument({ language: 'protocross', content: `var ${name} = 2;\n` });
    const inside = document.positionAt(document.getText().indexOf(name) + 3);

    const word = document.getWordRangeAtPosition(inside);

    assert.ok(word !== undefined, `nothing in '${name}' is a word to the editor`);
    assert.equal(document.getText(word), name);
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
      problems(source).every((problem) => String(typeof problem.code === 'object' ? problem.code.value : problem.code) !== 'PC2102'),
      `the extension's settings were reported as unknown: ${problems(source).map((problem) => problem.message).join(' | ')}`,
    );
  });

  // One save, never retried: retrying would pass a server that misses the first save it is told about,
  // which is the failure this is here to catch. What is waited out first is only VS Code's own start-up.
  it('refreshes diagnostics when an imported schema is saved, with no edit to the file', async () => {
    await workspaceWatcherRunning();
    const save = watchFor('shape.proto');

    try {
      fs.writeFileSync(workspaceFile('shape.proto'), 'syntax = "proto3";\n\nmessage Shape {\n  int64 height = 1;\n  int64 width = 2;\n}\n');

      await until(
        () => (problems(source).every((problem) => problem.severity !== vscode.DiagnosticSeverity.Error) ? true : undefined),
        () =>
          'the error to clear once width was added to the schema; ' +
          (save.seen ? 'VS Code did report the save to its watchers' : 'VS Code never reported the save to any watcher'),
      );
    } finally {
      save.dispose();
    }
  });

  it('starts the server outside every workspace folder', async () => {
    const protocross = await api();
    await running(protocross);

    const directory = protocross.controller.lastLaunch?.workingDirectory;
    assert.ok(directory !== undefined);
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
      const relative = path.relative(folder.uri.fsPath, directory);
      assert.ok(relative.startsWith('..') || path.isAbsolute(relative), `'${directory}' is inside '${folder.uri.fsPath}'`);
    }
  });

  it("reports the extension's version and this session's restarts in the status report", async () => {
    const protocross = await api();
    await running(protocross);

    const report = await protocross.collectStatus();

    assert.equal(report.fromServer, true);
    const version = vscode.extensions.getExtension(extensionId)?.packageJSON.version as string;
    assert.match(report.markdown, new RegExp(`\\| extension \\| ${version.replace(/\./g, '\\.')} \\|`));
    assert.match(report.markdown, /\| restarts this session \| \d+ \|/);
    assert.ok(report.privacy.length > 0 && report.markdown.includes(report.privacy));
  });

  it('recovers from the server crashing, without restarting the editor', async () => {
    const protocross = await api();
    const before = await running(protocross);
    const restarts = protocross.controller.restarts;
    assert.ok(before !== undefined, 'the server process must be known to be killed');

    process.kill(before);

    await until(
      () =>
        protocross.controller.state.kind === 'running' && protocross.controller.state.processId !== before ? true : undefined,
      'a new server process',
    );
    assert.equal(protocross.controller.restarts, restarts + 1);

    const report = await protocross.collectStatus();
    assert.match(report.markdown, new RegExp(`\\| restarts this session \\| ${restarts + 1} \\|`));
  });

  it('still produces a report when the server cannot start, saying why', async () => {
    const protocross = await api();
    const missing = path.join(path.dirname(workspaceFile('source.pcross')), 'no-such-server.dll');
    const configuration = vscode.workspace.getConfiguration('protocross');

    await configuration.update('server.path', missing, vscode.ConfigurationTarget.Global);

    try {
      await until(() => (protocross.controller.state.kind === 'failed' ? true : undefined), 'the launch to fail');

      const report = await protocross.collectStatus();

      assert.equal(report.fromServer, false);
      assert.match(report.markdown, /failed to start/);
      assert.ok(report.markdown.includes(missing), 'the report should name the server it could not find');
      assert.ok(report.markdown.includes(report.privacy));
    } finally {
      await configuration.update('server.path', undefined, vscode.ConfigurationTarget.Global);
    }

    await running(protocross);
  });
});
