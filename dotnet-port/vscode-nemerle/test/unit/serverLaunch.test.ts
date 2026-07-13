import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { after, before, test } from 'node:test';
import {
  DotNetRuntimeError,
  ensureDotNetRuntime,
  findRequiredRuntime,
  formatLaunchSpec,
  parseNetCoreAppRuntimes,
  resolveDotNetExecutable,
  resolveServerLaunch,
  ServerConfigurationError,
} from '../../src/serverLaunch';

let testDirectory: string;

before(async () => {
  testDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'nemerle-vscode-launch-'));
});

after(async () => {
  await fs.rm(testDirectory, { recursive: true, force: true });
});

test('DLL launch keeps executable and arguments separate', async () => {
  const serverPath = path.join(testDirectory, 'path with spaces', 'Nemerle.LanguageServer.dll');
  await fs.mkdir(path.dirname(serverPath), { recursive: true });
  await fs.writeFile(serverPath, 'test');

  const launch = await resolveServerLaunch(serverPath);
  assert.equal(launch.command, 'dotnet');
  assert.equal(launch.dotnetExecutable, 'dotnet');
  assert.deepEqual(launch.args, ['exec', path.normalize(serverPath)]);
  assert.equal(launch.cwd, path.dirname(path.normalize(serverPath)));
  assert.match(formatLaunchSpec(launch), /^dotnet exec ".*Nemerle\.LanguageServer\.dll"$/u);
});

test('absolute dotnet override is shared by DLL server launch and project query', async () => {
  const serverPath = path.join(testDirectory, 'Nemerle.LanguageServer.dll');
  const dotnetPath = path.join(testDirectory, 'custom dotnet.exe');
  await fs.writeFile(serverPath, 'test');
  await fs.writeFile(dotnetPath, 'test');
  const launch = await resolveServerLaunch(serverPath, dotnetPath);
  assert.equal(launch.command, path.normalize(dotnetPath));
  assert.equal(launch.dotnetExecutable, path.normalize(dotnetPath));
  assert.equal(await resolveDotNetExecutable('dotnet'), 'dotnet');
  await assert.rejects(() => resolveDotNetExecutable('relative-dotnet'), /absolute file path/u);
  await assert.rejects(
    () => resolveDotNetExecutable(path.join(testDirectory, 'missing-dotnet.exe')),
    /does not exist/u,
  );
});

test('native future server launch does not use a shell command string', async () => {
  const serverPath = path.join(testDirectory, 'nemerle-language-server.exe');
  await fs.writeFile(serverPath, 'test');

  const launch = await resolveServerLaunch(serverPath);
  assert.equal(launch.command, path.normalize(serverPath));
  assert.deepEqual(launch.args, []);
});

test('empty, relative, missing, and directory paths fail before process launch', async () => {
  await assert.rejects(() => resolveServerLaunch(''), ServerConfigurationError);
  await assert.rejects(() => resolveServerLaunch('relative.dll'), ServerConfigurationError);
  await assert.rejects(
    () => resolveServerLaunch(path.join(testDirectory, 'missing.dll')),
    /does not exist/u,
  );
  await assert.rejects(() => resolveServerLaunch(testDirectory), /is not a file/u);
});

test('empty configured path defaults to the bundled server', async () => {
  const bundledPath = path.join(testDirectory, 'server', 'Nemerle.LanguageServer.dll');
  await fs.mkdir(path.dirname(bundledPath), { recursive: true });
  await fs.writeFile(bundledPath, 'test');

  const launch = await resolveServerLaunch('', 'dotnet', bundledPath);
  assert.equal(launch.source, 'bundled');
  assert.equal(launch.command, 'dotnet');
  assert.deepEqual(launch.args, ['exec', path.normalize(bundledPath)]);
  assert.equal(launch.cwd, path.dirname(path.normalize(bundledPath)));
});

test('configured development path overrides the bundled server', async () => {
  const bundledPath = path.join(testDirectory, 'server', 'Nemerle.LanguageServer.dll');
  const configuredPath = path.join(testDirectory, 'dev', 'Nemerle.LanguageServer.dll');
  await fs.mkdir(path.dirname(bundledPath), { recursive: true });
  await fs.writeFile(bundledPath, 'test');
  await fs.mkdir(path.dirname(configuredPath), { recursive: true });
  await fs.writeFile(configuredPath, 'test');

  const launch = await resolveServerLaunch(configuredPath, 'dotnet', bundledPath);
  assert.equal(launch.source, 'configured');
  assert.equal(launch.serverPath, path.normalize(configuredPath));
});

test('a missing bundled server produces an actionable error', async () => {
  const bundledPath = path.join(testDirectory, 'missing-server', 'Nemerle.LanguageServer.dll');
  await assert.rejects(
    () => resolveServerLaunch('', 'dotnet', bundledPath),
    (error: unknown) =>
      error instanceof ServerConfigurationError &&
      /bundled Nemerle language server is missing/u.test(error.message) &&
      error.message.includes('pack-server.ps1'),
  );
});

test('parses Microsoft.NETCore.App runtimes out of dotnet --list-runtimes output', () => {
  const output = [
    'Microsoft.AspNetCore.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]',
    'Microsoft.NETCore.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]',
    'Microsoft.NETCore.App 10.0.9 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]',
    'Microsoft.WindowsDesktop.App 10.0.9 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]',
    '',
  ].join('\r\n');
  const versions = parseNetCoreAppRuntimes(output);
  assert.deepEqual(versions, ['8.0.11', '10.0.9']);
  assert.equal(findRequiredRuntime(versions, 10), '10.0.9');
  assert.equal(findRequiredRuntime(versions, 9), undefined);
  assert.deepEqual(parseNetCoreAppRuntimes(''), []);
});

test('ensureDotNetRuntime finds the installed .NET 10 runtime with the real host', async () => {
  const runtime = await ensureDotNetRuntime('dotnet');
  assert.match(runtime, /^10\./u);
  await assert.rejects(
    () => ensureDotNetRuntime('dotnet', 99),
    (error: unknown) => error instanceof DotNetRuntimeError && /99\.x runtime/u.test(error.message),
  );
});

test('ensureDotNetRuntime reports a missing dotnet host', async () => {
  const missingHost = path.join(testDirectory, 'no-such-dotnet.exe');
  await assert.rejects(
    () => ensureDotNetRuntime(missingHost),
    (error: unknown) => error instanceof DotNetRuntimeError && /was not found/u.test(error.message),
  );
});
