import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { runTests } from '@vscode/test-electron';

// Starts a real VS Code with this extension loaded from source and a copy of the fixture workspace
// open, and runs the suite inside it. A copy, because the tests write to the workspace -- saving a
// schema is one of the things being tested -- and a checkout must not change because a test ran.

async function main(): Promise<void> {
  const extensionRoot = path.resolve(__dirname, '..', '..', '..');
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'protolang-e2e-'));

  fs.cpSync(path.join(extensionRoot, 'test', 'fixtures', 'workspace'), workspace, { recursive: true });

  try {
    await runTests({
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
