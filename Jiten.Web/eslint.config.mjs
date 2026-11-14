// @ts-check
import withNuxt from './.nuxt/eslint.config.mjs';

export default withNuxt(
  {
    rules: {
      '@typescript-eslint/no-unused-vars': 'off',
      'vue/no-v-text-v-html-on-component': 'off',
      'vue/no-v-html': 'off',
      'vue/html-self-closing': ['warn', { html: { void: 'always', normal: 'always', component: 'always' } }],
      'vue/no-deprecated-filter': 'off',
      'no-empty': ['error', { allowEmptyCatch: true }],
      // Optional props here are typed `?:` and handled explicitly; forced defaults would change runtime values
      'vue/require-default-prop': 'off',
      // An unpinned locale formats numbers differently on the server and in the browser, so they change after hydration
      'no-restricted-syntax': [
        'error',
        {
          selector: "CallExpression[callee.property.name='toLocaleString']",
          message: 'Use formatNumber() from ~/utils/formatNumber for numbers and ~/utils/dateFormat for dates.',
        },
      ],
    },
  },
  {
    files: ['app/components/Tooltip.vue'],
    rules: { 'vue/multi-word-component-names': 'off' },
  }
);
