import {localeData} from './i18n/catalog.generated.js';

import {createTranslator,supportedLocale as resolveSupportedLocale} from './i18n-format.js';
export {createTranslator} from './i18n-format.js';
export const supportedLocale = (value,manifest=localeData.manifest) => resolveSupportedLocale(value,manifest);

export const localeManifest = localeData.manifest;
let current = createTranslator(localeData);
export const t = (key, values, fallback) => current.text(key,values,fallback);
export function useLocale(locale) {current = createTranslator(localeData,locale);return current;}

// Only explicitly marked presentation nodes are localized. Scientific values,
// names, notes and rendered evidence are never traversed or rewritten as prose.
export function localizeTree(root = document, translate = t) {
  const selector = '[data-i18n],[data-i18n-aria-label],[data-i18n-placeholder]';
  const elements = Array.from(root.querySelectorAll(selector));
  if (root.matches?.(selector)) elements.unshift(root);
  for (const element of elements) {
    if (element.dataset.i18n) element.textContent = translate(element.dataset.i18n);
    if (element.dataset.i18nAriaLabel) element.setAttribute('aria-label',translate(element.dataset.i18nAriaLabel));
    if (element.dataset.i18nPlaceholder) element.setAttribute('placeholder',translate(element.dataset.i18nPlaceholder));
  }
}
