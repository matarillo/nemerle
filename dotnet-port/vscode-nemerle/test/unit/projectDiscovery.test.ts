import assert from 'node:assert/strict';
import * as path from 'node:path';
import { test } from 'node:test';
import {
  isExcludedProjectPath,
  resolveSelectedProject,
  sortAndDedupeProjects,
} from '../../src/projectDiscovery';

test('project discovery excludes generated directories and sorts/deduplicates paths', () => {
  const root = path.resolve('workspace');
  const one = path.join(root, 'A', 'One.nproj');
  const two = path.join(root, 'B', 'Two.nproj');
  const excluded = path.join(root, 'A', 'obj', 'Generated.nproj');
  assert.equal(isExcludedProjectPath(excluded), true);
  assert.deepEqual(sortAndDedupeProjects([two, excluded, one, one]), [one, two]);
});

test('selection distinguishes zero, one, many, explicit, and stale choices', () => {
  const root = path.resolve('workspace');
  const one = path.join(root, 'One.nproj');
  const two = path.join(root, 'sub', 'Two.nproj');
  assert.deepEqual(resolveSelectedProject(root, [], '', undefined), {});
  assert.equal(resolveSelectedProject(root, [one], '', undefined).selected, one);
  assert.deepEqual(resolveSelectedProject(root, [one, two], '', undefined), {});
  assert.equal(resolveSelectedProject(root, [one, two], 'sub/Two.nproj', undefined).selected, two);
  assert.equal(resolveSelectedProject(root, [one, two], '', two).selected, two);
  assert.deepEqual(resolveSelectedProject(root, [one, two], '', path.join(root, 'stale.nproj')), {});
  assert.equal(
    resolveSelectedProject(root, [one, two], 'missing.nproj', undefined).configuredMissing,
    path.join(root, 'missing.nproj'),
  );
});
