import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { extensionOwnedSettings, withoutExtensionSettings } from '../../src/settings';

describe('what the server is sent of the protolang section', () => {
  it("takes out every setting that is the extension's own", () => {
    const answer = {
      includePaths: ['protos'],
      protocPath: '',
      logLevel: 'trace',
      server: { enabled: true, path: '' },
      dotnetPath: '',
    };

    const sent = withoutExtensionSettings(answer) as Record<string, unknown>;

    for (const name of extensionOwnedSettings) {
      assert.equal(Object.hasOwn(sent, name), false, `'${name}' was sent to the server`);
    }
  });

  it('keeps everything else, including a setting nobody declared, so the server can report a typo', () => {
    const sent = withoutExtensionSettings({ includePaths: ['protos'], includePath: 'typo', logLevel: 'info' });

    assert.deepEqual(sent, { includePaths: ['protos'], includePath: 'typo' });
  });

  it('does not change the answer it was handed', () => {
    const answer = { logLevel: 'info' };

    withoutExtensionSettings(answer);

    assert.deepEqual(answer, { logLevel: 'info' });
  });

  it('passes through anything that is not a settings object', () => {
    assert.equal(withoutExtensionSettings(null), null);
    assert.deepEqual(withoutExtensionSettings(['a']), ['a']);
    assert.equal(withoutExtensionSettings('text'), 'text');
  });
});
