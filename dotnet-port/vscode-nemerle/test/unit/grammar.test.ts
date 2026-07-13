import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { before, test } from 'node:test';
import { createOnigScanner, createOnigString, loadWASM } from 'vscode-oniguruma';
import { INITIAL, parseRawGrammar, Registry, type IGrammar } from 'vscode-textmate';

const extensionRoot = path.resolve(__dirname, '..', '..', '..');
const grammarPath = path.join(extensionRoot, 'syntaxes', 'nemerle.tmLanguage.json');
let grammar: IGrammar;

before(async () => {
  const wasmPath = require.resolve('vscode-oniguruma/release/onig.wasm');
  const wasm = fs.readFileSync(wasmPath);
  await loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));

  const registry = new Registry({
    onigLib: Promise.resolve({ createOnigScanner, createOnigString }),
    loadGrammar: async (scopeName) => {
      if (scopeName !== 'source.nemerle') {
        return null;
      }
      return parseRawGrammar(fs.readFileSync(grammarPath, 'utf8'), grammarPath);
    },
  });
  const loaded = await registry.loadGrammar('source.nemerle');
  assert.ok(loaded);
  grammar = loaded;
});

test('tokenizes declarations, core keywords, numbers, and types', () => {
  assertScope('module Demo { def value = 0xFFu; if (true) Demo(); }', 'module', 'storage.type');
  assertScope('module Demo { def value = 0xFFu; if (true) Demo(); }', 'def', 'keyword.declaration');
  assertScope('module Demo { def value = 0xFFu; if (true) Demo(); }', '0xFFu', 'constant.numeric.hex');
  assertScope('module Demo { def value = 0xFFu; if (true) Demo(); }', 'true', 'constant.language');
  assertScope('module Demo { def value = 0xFFu; if (true) Demo(); }', 'Demo', 'entity.name.type');
});

test('tokenizes comments, strings, chars, quotations, and splices', () => {
  assertScope('def text = $"value $(value)"; // note', '$"', 'string.quoted.double.interpolated');
  assertScope('def text = $"value $(value)"; // note', '$(', 'punctuation.section.interpolation');
  assertScope('def text = $"value $(value)"; // note', '// note', 'comment.line');
  assertScope("def ch = '\\n';", "'\\n'", 'string.quoted.single');
  assertScope('def tree = <[ ..$items ]>;', '<[', 'punctuation.section.embedded.begin');
  assertScope('def tree = <[ ..$items ]>;', '..$', 'punctuation.definition.variable');
  assertScope('def tree = <[ ..$items ]>;', ']>', 'punctuation.section.embedded.end');
});

test('tokenizes verbatim and nested recursive strings without an external grammar', () => {
  assertScope('def path = @"c:\\temp";', '@"', 'string.quoted.double.verbatim');
  assertScope('def raw = <#outer <#inner#> outer#>;', '<#inner#>', 'string.quoted.other.recursive');
});

function assertScope(line: string, text: string, expectedScope: string): void {
  const start = line.indexOf(text);
  assert.notEqual(start, -1, `fixture does not contain ${text}`);
  const end = start + text.length;
  const tokens = grammar.tokenizeLine(line, INITIAL).tokens.filter(
    (token) => token.startIndex < end && token.endIndex > start,
  );
  assert.ok(
    tokens.some((token) => token.scopes.some((scope) => scope.includes(expectedScope))),
    `Expected ${JSON.stringify(text)} to include scope ${expectedScope}; got ${JSON.stringify(tokens)}`,
  );
}
