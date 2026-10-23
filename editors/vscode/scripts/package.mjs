// Packages the extension into dist/protocross.vsix, with the README's relative links pointed at where
// its files are.
//
// vsce turns a relative link in the README into a link into the repository, but builds it from
// repository.url alone and drops repository.directory. This extension lives in editors/vscode, so the
// screenshot's link went to the repository's root, and the image was broken on the Marketplace and on
// Open VSX. The base URLs are derived here from those same two fields rather than written out again,
// so that a moved directory or a renamed repository cannot leave them behind. HEAD is what vsce would
// use itself: the default branch, as it stands when the page is viewed.

import { createVSIX } from '@vscode/vsce';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const extension = join(dirname(fileURLToPath(import.meta.url)), '..');
const { repository } = JSON.parse(readFileSync(join(extension, 'package.json'), 'utf8'));
const project = repository.url.replace(/\.git$/, '');

await createVSIX({
  cwd: extension,
  packagePath: join(extension, 'dist', 'protocross.vsix'),
  dependencies: false,
  baseContentUrl: `${project}/blob/HEAD/${repository.directory}`,
  baseImagesUrl: `${project}/raw/HEAD/${repository.directory}`,
});
