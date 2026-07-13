import * as path from 'node:path';
import Mocha from 'mocha';

export function run(): Promise<void> {
  const mocha = new Mocha({
    color: true,
    timeout: 90_000,
    ui: 'tdd',
  });
  const testFile = process.env.NEMERLE_TEST_MODE === 'untrusted'
    ? 'untrusted.test.js'
    : 'extension.test.js';
  mocha.addFile(path.resolve(__dirname, testFile));

  return new Promise((resolve, reject) => {
    mocha.run((failures) => {
      if (failures === 0) {
        resolve();
      } else {
        reject(new Error(`${failures} extension integration test(s) failed.`));
      }
    });
  });
}
