// Bundles the extension, and with --tests each test file, using esbuild.
//
// One bundle for the extension rather than tsc output plus node_modules: the language client and its
// dependencies are several hundred files, and VS Code loads one file faster than it walks a tree. Types
// are checked separately by `npm run typecheck`, since esbuild strips them without looking.

import { build, context } from 'esbuild';
import { readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const watch = process.argv.includes('--watch');
const tests = process.argv.includes('--tests');

/** Every .ts file under a directory, for the test entry points. */
function sources(directory) {
  return readdirSync(directory).flatMap((name) => {
    const full = join(directory, name);
    if (statSync(full).isDirectory()) {
      return sources(full);
    }
    return full.endsWith('.ts') ? [full] : [];
  });
}

const shared = {
  bundle: true,
  platform: 'node',
  format: 'cjs',
  target: 'node20',
  sourcemap: true,
  // vscode is supplied by the editor at run time; mocha and the test launcher are dev tools loaded
  // from node_modules rather than copied into each test bundle.
  external: ['vscode', 'mocha', '@vscode/test-electron'],
  logLevel: 'info',
};

const extension = { ...shared, entryPoints: ['src/extension.ts'], outfile: 'dist/extension.js' };

const testBundles = {
  ...shared,
  entryPoints: sources('test').map((file) => ({ in: file, out: relative('.', file).replace(/\.ts$/, '') })),
  outdir: 'out',
};

if (watch) {
  const watching = await context(extension);
  await watching.watch();
} else {
  await build(extension);
  if (tests) {
    await build(testBundles);
  }
}
