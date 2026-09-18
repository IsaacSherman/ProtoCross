// Puts the language server and the licence where the package expects them.
//
// The server is published framework-dependent and without an app host: one set of files that runs on
// every platform through the user's own dotnet, which is what lets this extension ship as one package
// rather than one per platform, and what leaves the choice of .NET install to the user. Its version is
// the extension's, so a status report names one number for both.

import { execFileSync } from 'node:child_process';
import { copyFileSync, readFileSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const extension = join(dirname(fileURLToPath(import.meta.url)), '..');
const repository = join(extension, '..', '..');
const { version } = JSON.parse(readFileSync(join(extension, 'package.json'), 'utf8'));
const output = join(extension, 'server');

rmSync(output, { recursive: true, force: true });

execFileSync(
  'dotnet',
  [
    'publish',
    join(repository, 'src', 'ProtoCross.LanguageServer', 'ProtoCross.LanguageServer.csproj'),
    '--configuration', 'Release',
    '--output', output,
    '--no-self-contained',
    '-p:UseAppHost=false',
    `-p:Version=${version}`,
  ],
  { stdio: 'inherit' },
);

// vsce requires a licence beside the manifest. Copied rather than kept twice in the repository.
copyFileSync(join(repository, 'LICENSE'), join(extension, 'LICENSE'));
