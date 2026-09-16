import contract from './contract.json';

/** The settings section, which the server also reads from. */
export const section = 'protolang';

/**
 * The top-level names under `protolang` that belong to this extension and mean nothing to the server:
 * how the server is started, and whether it is started at all.
 *
 * Kept in `contract.json` rather than here because the server's test suite reads the same file and
 * checks every setting the manifest declares is either one the server reads or one of these. A setting
 * added to the manifest and forgotten here would be sent to the server, which would report it on every
 * document as a setting it does not understand.
 */
export const extensionOwnedSettings: readonly string[] = contract.extensionOwnedSettings;

/** The sentence shown before a report the extension wrote itself is copied. */
export const privacyNote: string = contract.privacyNote;

/**
 * One `workspace/configuration` answer with this extension's own settings taken out.
 *
 * The server is strict about the section on purpose: a setting it does not read is reported, so a
 * user who writes `protolang.includePath` is told about the typo. Filtering here keeps that strictness
 * and keeps the server from being told about settings that are this extension's business. Anything that
 * is not a settings object is passed through untouched.
 */
export function withoutExtensionSettings(answer: unknown): unknown {
  if (answer === null || typeof answer !== 'object' || Array.isArray(answer)) {
    return answer;
  }

  const kept: Record<string, unknown> = { ...(answer as Record<string, unknown>) };

  for (const name of extensionOwnedSettings) {
    delete kept[name];
  }

  return kept;
}
