import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { privacyNote } from '../../src/settings';
import { renderReport } from '../../src/statusReport';

describe("the extension's own status report", () => {
  it('opens with the title and the privacy note, before any fact', () => {
    const report = renderReport(privacyNote, [{ title: 'Server', facts: [{ label: 'state', value: 'failed' }] }]);

    assert.ok(report.startsWith(`# ProtoCross language server status\n\n${privacyNote}\n`));
    assert.ok(report.indexOf(privacyNote) < report.indexOf('failed'));
  });

  it('keeps a value with a pipe or a newline inside its own table cell', () => {
    const report = renderReport(privacyNote, [
      { title: 'Server', facts: [{ label: 'reason', value: 'a | b\nc', source: 'x' }] },
    ]);

    const row = report.split('\n').find((line) => line.startsWith('| reason '));
    assert.equal(row, '| reason | a \\| b c | x |');
  });

  it('puts the note after the facts of its section', () => {
    const report = renderReport(privacyNote, [
      { title: 'Server', facts: [{ label: 'state', value: 'failed' }], note: 'Why it failed.' },
    ]);

    assert.ok(report.indexOf('| state | failed |') < report.indexOf('Why it failed.'));
  });
});
