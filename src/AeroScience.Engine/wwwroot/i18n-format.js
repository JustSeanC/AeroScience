// This formatter uses the bounded shared contract, not DOM or Intl APIs. Native
// and .NET adapters can consume the same data without importing browser helpers.
export function supportedLocale(value, manifest) {
  const match = manifest.locales.find(item => item.id.toLowerCase() === String(value || '').toLowerCase());
  return match ? match.id : manifest.fallback;
}
const own = (object,key) => !!object && Object.prototype.hasOwnProperty.call(object,key);
function one(locale, count) {
  return ['fr-FR','pt-BR'].includes(locale) ? count >= 0 && count < 2 : count === 1;
}
export function createTranslator(data, requestedLocale = 'en-US', {development = false, onMissing} = {}) {
  const locale = supportedLocale(requestedLocale,data.manifest), fallback = data.manifest.fallback;
  function text(key, values = {}, fallbackText) {
    const available = data.catalogs[locale], english = data.catalogs[fallback];
    const selected = available && available.version === data.manifest.catalogVersion && own(available.messages,key) ? available : english;
    const message = own(selected.messages,key) ? selected.messages[key] : undefined;
    const rule = own(data.schema.messages,key) ? data.schema.messages[key] : null;
    const missing = !rule || message === undefined || rule.plural && (!Number.isInteger(values[rule.plural]) || values[rule.plural] < 0) || rule.parameters.some(name => !own(values,name)
      || !['string','number'].includes(typeof values[name]) || typeof values[name] === 'number' && !Number.isFinite(values[name]));
    if (missing) {
      if (development) {if (onMissing) onMissing(key);return '['+key+']';}
      return fallbackText || english.messages['common.messageUnavailable'];
    }
    const template = typeof message === 'string' ? message : one(selected.locale,Number(values[rule.plural])) ? message.one : message.other;
    // A function replacement preserves literal $&, Unicode names, UTC, hashes and units.
    return template.replace(/\{([a-z][a-zA-Z0-9]*)\}/g, (_match,name) => String(values[name]));
  }
  return Object.freeze({locale, effectiveLocale:data.catalogs[locale] && data.catalogs[locale].version === data.manifest.catalogVersion ? locale : fallback, text});
}
