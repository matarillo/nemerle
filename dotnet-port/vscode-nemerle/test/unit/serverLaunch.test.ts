import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { after, before, test } from 'node:test';
import {
  formatLaunchSpec,
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
  assert.deepEqual(launch.args, ['exec', path.normalize(serverPath)]);
  assert.equal(launch.cwd, path.dirname(path.normalize(serverPath)));
  assert.match(formatLaunchSpec(launch), /^dotnet exec ".*Nemerle\.LanguageServer\.dll"$/u);
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
