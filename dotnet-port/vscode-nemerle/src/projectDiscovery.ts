import * as path from 'node:path';

const excludedSegments = new Set(['bin', 'obj', 'node_modules', '.git', '.vs', 'dist']);

export function isExcludedProjectPath(projectPath: string): boolean {
  return path.normalize(projectPath).split(path.sep).some((segment) =>
    excludedSegments.has(segment.toLowerCase()));
}

export function sortAndDedupeProjects(projectPaths: readonly string[]): string[] {
  const values = new Map<string, string>();
  for (const projectPath of projectPaths) {
    const normalized = path.normalize(path.resolve(projectPath));
    if (!isExcludedProjectPath(normalized)) {
      values.set(process.platform === 'win32' ? normalized.toLowerCase() : normalized, normalized);
    }
  }
  return [...values.values()].sort((left, right) => left.localeCompare(right));
}

export function resolveSelectedProject(
  workspaceRoot: string,
  candidates: readonly string[],
  configuredProject: string,
  storedProject: string | undefined,
): { selected?: string; configuredMissing?: string } {
  const comparer = (value: string): string => process.platform === 'win32' ? value.toLowerCase() : value;
  const byPath = new Map(candidates.map((candidate) => [comparer(candidate), candidate]));
  const configured = configuredProject.trim();
  if (configured.length > 0) {
    const resolved = path.normalize(path.isAbsolute(configured)
      ? configured
      : path.resolve(workspaceRoot, configured));
    return byPath.has(comparer(resolved))
      ? { selected: byPath.get(comparer(resolved)) }
      : { configuredMissing: resolved };
  }
  if (storedProject !== undefined) {
    const selected = byPath.get(comparer(path.normalize(storedProject)));
    if (selected !== undefined) {
      return { selected };
    }
  }
  return candidates.length === 1 ? { selected: candidates[0] } : {};
}
