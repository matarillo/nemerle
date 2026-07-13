import assert from 'node:assert/strict';
import * as fsp from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { after, before, test } from 'node:test';
import {
  listServerAssemblies,
  noticeIdentifierFor,
  requiredServerFiles,
  verifyBundledServer,
} from '../tools/verifyBundledServer';

let testDirectory: string;

before(async () => {
  testDirectory = await fsp.mkdtemp(path.join(os.tmpdir(), 'nemerle-vscode-bundle-'));
});

after(async () => {
  await fsp.rm(testDirectory, { recursive: true, force: true });
});

test('satellite resource assemblies map to their parent package identifier', () => {
  assert.equal(
    noticeIdentifierFor(path.join('server', 'ja', 'Microsoft.VisualStudio.Threading.resources.dll')),
    'Microsoft.VisualStudio.Threading',
  );
  assert.equal(
    noticeIdentifierFor(path.join('server', 'Newtonsoft.Json.dll')),
    'Newtonsoft.Json',
  );
});

test('verification reports missing closure files and uncovered assemblies', async () => {
  const serverDir = path.join(testDirectory, 'server');
  await fsp.mkdir(path.join(serverDir, 'runtimes', 'win'), { recursive: true });
  for (const name of requiredServerFiles) {
    await fsp.writeFile(path.join(serverDir, name), 'x');
  }
  await fsp.writeFile(path.join(serverDir, 'runtimes', 'win', 'Uncovered.Assembly.dll'), 'x');
  const noticesPath = path.join(testDirectory, 'THIRD-PARTY-NOTICES.md');
  const covered = requiredServerFiles
    .filter((name) => name.endsWith('.dll'))
    .map((name) => path.basename(name, '.dll'))
    .join('\n');
  await fsp.writeFile(noticesPath, covered);

  const problems = verifyBundledServer(serverDir, noticesPath);
  assert.deepEqual(problems, [
    'Bundled assembly is not covered by THIRD-PARTY-NOTICES.md: Uncovered.Assembly',
  ]);

  await fsp.rm(path.join(serverDir, 'Nemerle.LanguageServer.runtimeconfig.json'));
  const problemsAfterRemoval = verifyBundledServer(serverDir, noticesPath);
  assert.ok(problemsAfterRemoval.some((problem) =>
    problem.includes('Missing required bundled server file: Nemerle.LanguageServer.runtimeconfig.json')));

  assert.deepEqual(
    verifyBundledServer(path.join(testDirectory, 'no-server'), noticesPath),
    [`Bundled server directory does not exist: ${path.join(testDirectory, 'no-server')}. ` +
      'Run dotnet-port\\vscode-nemerle\\pack-server.ps1 first.'],
  );
});

test('the staged server, when present, passes the packaging verification', () => {
  const extensionRoot = path.resolve(__dirname, '..', '..', '..');
  const serverDir = path.join(extensionRoot, 'server');
  const noticesPath = path.join(extensionRoot, 'THIRD-PARTY-NOTICES.md');
  const problems = verifyBundledServer(serverDir, noticesPath);
  const stagedMissing = problems.length === 1 && problems[0]!.includes('does not exist');
  if (stagedMissing) {
    // pack-server.ps1 has not run in this checkout; `npm run verify-server`
    // enforces the full check at package time.
    return;
  }
  assert.deepEqual(problems, []);
  assert.ok(listServerAssemblies(serverDir).length >= requiredServerFiles.filter((f) => f.endsWith('.dll')).length);
});
