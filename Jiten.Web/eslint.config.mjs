// @ts-check
import withNuxt from './.nuxt/eslint.config.mjs';

export default withNuxt({
  rules: {
    '@typescript-eslint/no-unused-vars': 'off',
    'vue/no-v-text-v-html-on-component': 'off',
    'vue/no-v-html': 'off',
  },
});
