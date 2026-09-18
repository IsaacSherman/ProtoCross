import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  dotnetCandidates,
  hasSupportedRuntime,
  initializationOptions,
  isAbsoluteLocation,
  minimumDotnetMajor,
  parseRuntimes,
  sanitizeEnvironment,
  serverCommand,
} from '../../src/launch';

// The launch rules #45 states, each as a property. None of these touches VS Code or starts a process;
// test/server does both.

describe('which PATH entries say where they are on their own', () => {
  const windows: [string, boolean][] = [
    ['C:\\Program Files\\dotnet', true],
    ['c:/tools/bin', true],
    ['"C:\\Program Files\\protoc\\bin"', true],
    ['\\\\server\\share\\tools', true],
    ['\\\\?\\C:\\very\\long', true],
    ['.', false],
    ['..', false],
    ['bin', false],
    ['.\\tools', false],
    ['\\tools', false],
    ['C:tools', false],
    ['', false],
  ];

  for (const [entry, absolute] of windows) {
    it(`on Windows, '${entry}' is ${absolute ? 'absolute' : 'relative'}`, () => {
      assert.equal(isAbsoluteLocation(entry, 'win32'), absolute);
    });
  }

  const posix: [string, boolean][] = [
    ['/usr/local/bin', true],
    ['/', true],
    ['.', false],
    ['./node_modules/.bin', false],
    ['bin', false],
    ['~/bin', false],
    ['', false],
  ];

  for (const [entry, absolute] of posix) {
    it(`on Linux and macOS, '${entry}' is ${absolute ? 'absolute' : 'relative'}`, () => {
      assert.equal(isAbsoluteLocation(entry, 'linux'), absolute);
      assert.equal(isAbsoluteLocation(entry, 'darwin'), absolute);
    });
  }
});

describe('the environment the server is started with', () => {
  it('keeps every absolute PATH entry, in order, so an installed protoc is still found', () => {
    const { env } = sanitizeEnvironment({ PATH: '/opt/protoc/bin:.:/usr/bin::bin:/bin' }, 'linux');

    assert.equal(env.PATH, '/opt/protoc/bin:/usr/bin:/bin');
  });

  it('reports each relative entry it removed, as written, and not the empty ones', () => {
    const { removedPathEntries } = sanitizeEnvironment({ PATH: '.:/usr/bin::./tools:..:' }, 'linux');

    assert.deepEqual(removedPathEntries, ['.', './tools', '..']);
  });

  it("finds Windows' PATH under whatever case it was stored in, and keeps that one key", () => {
    const { env, removedPathEntries } = sanitizeEnvironment({ Path: 'C:\\Windows;.;C:\\tools' }, 'win32');

    assert.equal(env.Path, 'C:\\Windows;C:\\tools');
    assert.equal(env.PATH, undefined);
    assert.deepEqual(removedPathEntries, ['.']);
  });

  it('drops a relative PROTOCROSS_PROTOC and NUGET_PACKAGES, and says which', () => {
    const { env, removedVariables } = sanitizeEnvironment(
      { PROTOCROSS_PROTOC: 'tools/protoc', NUGET_PACKAGES: '.nuget', PATH: '/usr/bin' },
      'linux',
    );

    assert.equal(env.PROTOCROSS_PROTOC, undefined);
    assert.equal(env.NUGET_PACKAGES, undefined);
    assert.deepEqual([...removedVariables].sort(), ['NUGET_PACKAGES', 'PROTOCROSS_PROTOC']);
  });

  it('forwards an absolute PROTOCROSS_PROTOC and NUGET_PACKAGES untouched', () => {
    const { env, removedVariables } = sanitizeEnvironment(
      { PROTOCROSS_PROTOC: 'C:\\protoc\\bin\\protoc.exe', nuget_packages: 'D:\\packages' },
      'win32',
    );

    assert.equal(env.PROTOCROSS_PROTOC, 'C:\\protoc\\bin\\protoc.exe');
    assert.equal(env.nuget_packages, 'D:\\packages');
    assert.deepEqual(removedVariables, []);
  });

  it('leaves every other variable exactly as it was', () => {
    const source = { PATH: '/usr/bin', HOME: '/home/someone', DOTNET_ROOT: '/usr/share/dotnet', ODD: '.' };

    const { env } = sanitizeEnvironment(source, 'linux');

    assert.deepEqual(env, source);
  });
});

describe('where dotnet is looked for', () => {
  it('produces only absolute candidates, whatever PATH holds', () => {
    const candidates = dotnetCandidates({ PATH: '.:bin:/usr/bin', DOTNET_ROOT: 'relative/dotnet' }, 'linux', '/home/someone');

    assert.ok(candidates.length > 0);
    for (const candidate of candidates) {
      assert.ok(isAbsoluteLocation(candidate, 'linux'), `'${candidate}' is not absolute`);
    }
  });

  it('tries DOTNET_ROOT, then PATH, then the usual places, then the home directory', () => {
    const candidates = dotnetCandidates(
      { DOTNET_ROOT: '/custom/dotnet', PATH: '/usr/bin' },
      'linux',
      '/home/someone',
    );

    assert.equal(candidates[0], '/custom/dotnet/dotnet');
    assert.equal(candidates[1], '/usr/bin/dotnet');
    assert.ok(candidates.includes('/usr/share/dotnet/dotnet'));
    assert.equal(candidates.at(-1), '/home/someone/.dotnet/dotnet');
  });

  it('looks for dotnet.exe in Program Files on Windows', () => {
    const candidates = dotnetCandidates({ ProgramFiles: 'C:\\Program Files', PATH: '' }, 'win32', 'C:\\Users\\someone');

    assert.ok(candidates.includes('C:\\Program Files\\dotnet\\dotnet.exe'));
  });
});

describe('which runtimes can run the server', () => {
  const listing = [
    'Microsoft.AspNetCore.App 8.0.20 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]',
    'Microsoft.NETCore.App 8.0.20 [/usr/share/dotnet/shared/Microsoft.NETCore.App]',
    'Microsoft.NETCore.App 9.0.9 [/usr/share/dotnet/shared/Microsoft.NETCore.App]',
    'Microsoft.WindowsDesktop.App 10.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]',
  ].join('\r\n');

  it('reads only the core runtime from the listing', () => {
    assert.deepEqual(parseRuntimes(listing), ['8.0.20', '9.0.9']);
  });

  it(`refuses a machine whose newest core runtime is older than ${minimumDotnetMajor}`, () => {
    assert.equal(hasSupportedRuntime(parseRuntimes(listing)), false);
  });

  it(`accepts ${minimumDotnetMajor} and anything newer, which the server rolls forward to`, () => {
    assert.equal(hasSupportedRuntime([`${minimumDotnetMajor}.0.0`]), true);
    assert.equal(hasSupportedRuntime([`${minimumDotnetMajor + 1}.0.0`]), true);
  });

  it(`does not accept a preview of ${minimumDotnetMajor}, which roll-forward will not choose`, () => {
    assert.equal(hasSupportedRuntime([`${minimumDotnetMajor}.0.0-rc.2.25502.107`]), false);
  });

  it('does not accept a preview of a newer major either, for the same reason', () => {
    assert.equal(hasSupportedRuntime([`${minimumDotnetMajor + 1}.0.0-preview.1.25080.5`]), false);
    assert.equal(hasSupportedRuntime([`${minimumDotnetMajor + 1}.0.0-preview.1.25080.5`, `${minimumDotnetMajor}.0.4`]), true);
  });
});

describe('the command that starts the server', () => {
  it('runs an assembly through dotnet, by the paths it was given', () => {
    assert.deepEqual(serverCommand('/ext/server/protocross-server.dll', '/usr/bin/dotnet', 'trace'), {
      command: '/usr/bin/dotnet',
      args: ['/ext/server/protocross-server.dll', '--log-level=trace'],
    });
  });

  it('starts an executable directly', () => {
    assert.deepEqual(serverCommand('/opt/protocross/protocross-server', undefined, 'info'), {
      command: '/opt/protocross/protocross-server',
      args: ['--log-level=info'],
    });
  });

  it('refuses to run an assembly with no dotnet rather than searching for one', () => {
    assert.throws(() => serverCommand('/ext/server/protocross-server.dll', undefined, 'info'));
  });
});

describe('what the server is told at initialize', () => {
  it('always states whether the workspace is trusted, including when it is not', () => {
    const options = initializationOptions('isaacsherman.protocross', '0.1.0', false, []);

    assert.equal(Object.hasOwn(options, 'workspaceTrusted'), true);
    assert.equal(options.workspaceTrusted, false);
  });
});
