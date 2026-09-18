/**
 * The status report the extension writes when the server cannot write its own.
 *
 * Laid out as the server lays out its report -- a title, the privacy note, then sections of labelled
 * facts -- so that somebody pasting either into an issue produces something that reads the same way.
 * Kept free of VS Code so a test can check what it says about a server that never started.
 */

/** One labelled value, and where it came from when that is worth saying. */
export interface Fact {
  readonly label: string;
  readonly value: string;
  readonly source?: string;
}

export interface Section {
  readonly title: string;
  readonly facts: readonly Fact[];
  readonly note?: string;
}

/** A report as the status command shows and copies it. */
export interface StatusReport {
  readonly markdown: string;
  readonly privacy: string;
  /** Whether the server wrote it, or the extension wrote it because the server could not. */
  readonly fromServer: boolean;
}

/** Renders `sections` the way the server renders its own report. */
export function renderReport(privacy: string, sections: readonly Section[]): string {
  let report = `# ProtoCross language server status\n\n${privacy}\n`;

  for (const section of sections) {
    report += `\n## ${section.title}\n\n`;

    if (section.facts.length > 0) {
      report += '| | | |\n|---|---|---|\n';
      for (const fact of section.facts) {
        report += `| ${fact.label} | ${cell(fact.value)} | ${cell(fact.source ?? '')} |\n`;
      }
    }

    if (section.note !== undefined) {
      report += `${section.facts.length > 0 ? '\n' : ''}${section.note}\n`;
    }
  }

  return report;
}

/** A value that cannot break the table it sits in. */
function cell(value: string): string {
  return value.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}
