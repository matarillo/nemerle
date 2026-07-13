import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

const extensionRoot = path.resolve(__dirname, '..', '..', '..');

test('manifest registers Nemerle language, commands, settings, and restricted mode', () => {
  const manifest = readJson<Record<string, any>>('package.json');
  const language = manifest.contributes.languages[0];
  assert.equal(language.id, 'nemerle');
  assert.deepEqual(language.extensions, ['.n']);
  assert.equal(language.configuration, './language-configuration.json');

  const grammar = manifest.contributes.grammars[0];
  assert.equal(grammar.language, 'nemerle');
  assert.equal(grammar.scopeName, 'source.nemerle');
  assert.ok(fs.existsSync(path.join(extensionRoot, grammar.path)));

  const commands = new Set(
    manifest.contributes.commands.map((command: { command: string }) => command.command),
  );
  assert.ok(commands.has('nemerle.restartLanguageServer'));
  assert.ok(commands.has('nemerle.showOutput'));

  const properties = manifest.contributes.configuration.properties;
  assert.equal(properties['nemerle.server.path'].type, 'string');
  assert.deepEqual(properties['nemerle.server.trace'].enum, ['off', 'messages', 'verbose']);

  const trust = manifest.capabilities.untrustedWorkspaces;
  assert.equal(trust.supported, 'limited');
  assert.deepEqual(trust.restrictedConfigurations, ['nemerle.server.path']);
});

test('language configuration provides comments, brackets, closing, surrounding, and indentation', () => {
  const configuration = readJson<Record<string, any>>('language-configuration.json');
  assert.equal(configuration.comments.lineComment, '//');
  assert.deepEqual(configuration.comments.blockComment, ['/*', '*/']);
  assert.ok(configuration.brackets.length >= 4);
  assert.ok(configuration.autoClosingPairs.length >= 6);
  assert.ok(configuration.autoSurroundingPairs.length >= 6);
  assert.equal(typeof configuration.indentationRules.increaseIndentPattern, 'string');
  assert.equal(typeof configuration.indentationRules.decreaseIndentPattern, 'string');
});

function readJson<T>(relativePath: string): T {
  return JSON.parse(fs.readFileSync(path.join(extensionRoot, relativePath), 'utf8')) as T;
}
