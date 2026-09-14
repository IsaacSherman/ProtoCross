import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, it } from 'node:test';
import { initializationOptions, sanitizeEnvironment } from '../../src/launch';
import {
  fileNamedProtoc,
  pathSeparator,
  Server,
  tattletale,
  temporaryDirectory,
  type StatusSection,
} from './harness';

// The real server, started the way the extension starts it, on the properties #45 states about the
// launch. Each property that is a defence is paired with the same launch made without the defence, so a
// passing test says the defence mattered rather than that the attack never worked.

const installationPage = 'https://protobuf.dev/installation/';

/** A workspace holding one ProtoLang file that imports one schema. */
function workspace(): { folder: string; source: string } {
  const folder = temporaryDirectory('workspace');
  fs.writeFileSync(path.join(folder, 'shape.proto'), 'syntax = "proto3";\nmessage Shape { int64 width = 1; }\n');

  const source = path.join(folder, 'source.protolang');
  fs.writeFileSync(
    source,
    'import proto "shape.proto";\n\nextend Shape {\n    fn doubled() -> int64 {\n        return width * 2;\n    }\n}\n',
  );

  return { folder, source };
}

/** This process's environment with `entries` in front of PATH, as a user's shell might have it. */
function environmentWithPath(...entries: string[]): NodeJS.ProcessEnv {
  const env = { ...process.env };
  const key = Object.keys(env).find((name) => name.toUpperCase() === 'PATH') ?? 'PATH';
  env[key] = [...entries, env[key] ?? ''].join(pathSeparator);
  delete env.PROTOLANG_PROTOC;
  return env;
}

function pathFact(section: StatusSection): string {
  const fact = section.facts.find((candidate) => candidate.label === 'path');
  assert.ok(fact !== undefined, 'the protoc section must name a path');
  return fact.value;
}

describe('the launch keeps the workspace from choosing which protoc runs', { timeout: 120_000 }, () => {
  it('a protoc beside the working directory is found through "." when nothing is done about it', async () => {
    const { folder } = workspace();
    const committed = fileNamedProtoc(folder);

    // The launch without the defence: started in the workspace, with "." on PATH as it was inherited.
    const server = await Server.start({
      env: environmentWithPath('.') as Record<string, string>,
      cwd: folder,
      initializationOptions: initializationOptions('test', '0.0.0', true, []),
    });

    try {
      assert.equal(
        path.resolve(folder, pathFact(await server.protoc())),
        committed,
        'without the launch rules a file committed to the workspace is the protoc the server picks, which is the threat',
      );
    } finally {
      await server.stop();
    }
  });

  it('started as the extension starts it, that protoc is not the one found', async () => {
    const { folder } = workspace();
    const committed = fileNamedProtoc(folder);
    const { env, removedPathEntries } = sanitizeEnvironment(environmentWithPath('.'), process.platform);

    const server = await Server.start({
      env,
      cwd: temporaryDirectory('extension-storage'),
      initializationOptions: initializationOptions('test', '0.0.0', true, removedPathEntries),
    });

    try {
      const found = pathFact(await server.protoc());
      assert.notEqual(path.resolve(folder, found), committed);
      assert.ok(!found.startsWith('.'), `'${found}' was found through a relative entry`);
    } finally {
      await server.stop();
    }
  });

  it('a protoc on an absolute PATH entry is still found', async () => {
    const installed = fileNamedProtoc(temporaryDirectory('installed-protoc'));
    const { env, removedPathEntries } = sanitizeEnvironment(
      environmentWithPath('.', path.dirname(installed)),
      process.platform,
    );

    const server = await Server.start({
      env,
      cwd: temporaryDirectory('extension-storage'),
      initializationOptions: initializationOptions('test', '0.0.0', true, removedPathEntries),
    });

    try {
      const section = await server.protoc();
      assert.equal(pathFact(section), installed);
      assert.equal(section.facts.find((fact) => fact.label === 'path')?.source, 'found on PATH');
    } finally {
      await server.stop();
    }
  });
});

describe('an untrusted workspace, under the extension', { timeout: 120_000 }, () => {
  it('does not run the protoc its own settings name until the workspace is trusted', async () => {
    const { folder, source } = workspace();
    const marker = path.join(temporaryDirectory('marker'), 'ran');
    const committed = tattletale(folder, marker);
    const { env, removedPathEntries } = sanitizeEnvironment(process.env, process.platform);

    const server = await Server.start({
      env,
      cwd: temporaryDirectory('extension-storage'),
      folder,
      settings: { protocPath: committed },
      initializationOptions: initializationOptions('test', '0.0.0', false, removedPathEntries),
    });

    try {
      const uri = await server.open(source);
      await server.until(() => server.published.some((published) => published.uri === uri), 'diagnostics for the document');
      await server.status();

      assert.equal(fs.existsSync(marker), false, 'a protoc named by an untrusted workspace ran before trust was granted');

      // The same session, one notification later: the file appearing is what shows its absence meant something.
      await server.connection.sendNotification('protolang/didChangeWorkspaceTrust', { trusted: true });
      await server.until(() => fs.existsSync(marker), 'the named protoc run once the workspace was trusted');
    } finally {
      await server.stop();
    }
  });
});

describe('a machine with no protoc', { timeout: 120_000 }, () => {
  // On Windows the package cache is found under the profile directory the operating system reports, and
  // no environment variable moves it. A developer machine that has restored this repository has a protoc
  // there, so the property can only be shown where that cache is empty -- which is every CI runner the
  // extension job uses, since that job restores nothing that carries one.
  const cache = path.join(os.homedir(), '.nuget', 'packages', 'grpc.tools');
  const skip = process.platform === 'win32' && fs.existsSync(cache)
    ? `this machine has a protoc in '${cache}', which cannot be hidden from the server on Windows`
    : false;

  it('is told where to get one, and which relative PATH entries were skipped', { skip }, async () => {
    const nowhere = temporaryDirectory('empty');
    const machine: NodeJS.ProcessEnv = {
      ...process.env,
      NUGET_PACKAGES: nowhere,
      HOME: nowhere,
      USERPROFILE: nowhere,
    };
    delete machine.PROTOLANG_PROTOC;
    const key = Object.keys(machine).find((name) => name.toUpperCase() === 'PATH') ?? 'PATH';
    machine[key] = ['.', 'tools', nowhere].join(pathSeparator);

    const { env, removedPathEntries } = sanitizeEnvironment(machine, process.platform);

    const server = await Server.start({
      env,
      cwd: temporaryDirectory('extension-storage'),
      initializationOptions: initializationOptions('test', '0.0.0', true, removedPathEntries),
    });

    try {
      const note = (await server.protoc()).note ?? '';

      assert.ok(note.includes(installationPage), `the note does not say where protoc is published: ${note}`);
      assert.ok(note.includes('protolang.protocPath'), `the note does not name the setting: ${note}`);
      assert.ok(note.includes("'.'") && note.includes("'tools'"), `the note does not name the skipped entries: ${note}`);
    } finally {
      await server.stop();
    }
  });
});
