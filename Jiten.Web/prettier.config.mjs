/**
 * @see https://prettier.io/docs/en/configuration.html
 * @type {import("prettier").Config}
 */
const config = {
  trailingComma: 'es5',
  tabWidth: 2,
  semi: true,
  singleQuote: true,
  bracketSameLine: false,
  vueIndentScriptAndStyle: true,
  printWidth: 160,
  endOfLine: 'auto',
  htmlWhitespaceSensitivity: 'ignore',
};

export default config;
