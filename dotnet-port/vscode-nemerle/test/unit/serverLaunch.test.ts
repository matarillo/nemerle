import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { after, before, test } from 'node:test';
import {
  formatLaunchSpec,
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
