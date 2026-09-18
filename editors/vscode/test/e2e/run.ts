import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { runTests } from '@vscode/test-electron';
import pinned from './vscode-version.json';

// Starts a real VS Code with this extension loaded from source and a copy of the fixture workspace
// open, and runs the suite inside it. A copy, because the tests write to the workspace -- saving a
// schema is one of the things being tested -- and a checkout must not change because a test ran.
//
// The release is pinned rather than left to follow stable. Following it meant a new VS Code arrived
// unannounced about once a month -- another download of about a gigabyte unpacked -- and a suite that
// went red on the day it did would look exactly like a change here had broken it. Pinned, the suite
// goes red for a VS Code release in the commit that moves this number, which is where somebody is
// looking for it. The version lives in its own file because CI keys its cache of the download on that
// file alone.

async function main(): Promise<void> {
  const extensionRoot = path.resolve(__dirname, '..', '..', '..');
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'protocross-e2e-'));

  fs.cpSync(path.join(extensionRoot, 'test', 'fixtures', 'workspace'), workspace, { recursive: true });

  try {
    await runTests({
      version: pinned.version,
      extensionDevelopmentPath: extensionRoot,
      extensionTestsPath: path.join(__dirname, 'suite', 'index.js'),
      launchArgs: [workspace, '--disable-extensions', '--skip-welcome', '--skip-release-notes'],
    });
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
}

void main();
