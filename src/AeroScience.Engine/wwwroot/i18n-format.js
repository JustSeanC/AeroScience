// This formatter uses the bounded shared contract, not DOM or Intl APIs. Native
// and .NET adapters can consume the same data without importing browser helpers.
export function supportedLocale(value, manifest) {
  const match = manifest.locales.find(item => item.id.toLowerCase() === String(value || '').toLowerCase());
  return match ? match.id : manifest.fallback;
}
const own = (object,key) => !!object && Object.prototype.hasOwnProperty.call(object,key);
export function pluralCategory(locale,count) {
  // Bounded contract: nonnegative integer counts. CLDR decimal operands are
  // deliberately excluded; scientific decimals use explicit value messages.
  if(['es-ES','fr-FR','pt-BR'].includes(locale)&&count!==0&&count%1000000===0)return 'many';
  return (['fr-FR','pt-BR'].includes(locale)?count===0||count===1:count===1)?'one':'other';
}
export function createTranslator(data, requestedLocale = 'en-US', {development = false, onMissing} = {}) {
  const locale = supportedLocale(requestedLocale,data.manifest), fallback = data.manifest.fallback;
  const compatible=catalog=>!!catalog&&catalog.locale===locale&&catalog.version===data.manifest.catalogVersion
    &&(locale===fallback||data.manifest.qualityContract!==1||catalog.sourceHash===data.manifest.catalogIdentity);
  const catalogFor=key=>compatible(data.catalogs[locale])&&own(data.catalogs[locale].messages,key)
    &&!(data.catalogs[locale].pendingEnglishFallbackKeys||[]).includes(key)?data.catalogs[locale]:data.catalogs[fallback];
  function text(key, values = {}, fallbackText) {
    const available = data.catalogs[locale], english = data.catalogs[fallback];
    let selected = catalogFor(key);
    let message = own(selected.messages,key) ? selected.messages[key] : undefined;
    if(message===null||typeof message!=='string'&&typeof message!=='object'){selected=english;message=english.messages[key];}
    const rule = own(data.schema.messages,key) ? data.schema.messages[key] : null;
    const missing = !rule || message === undefined || rule.plural && (!Number.isSafeInteger(values[rule.plural]) || values[rule.plural] < 0) || rule.parameters.some(name => !own(values,name)
      || !['string','number'].includes(typeof values[name]) || typeof values[name] === 'number' && !Number.isFinite(values[name]));
    if (missing) {
      if (development) {if (onMissing) onMissing(key);return '['+key+']';}
      return fallbackText || english.messages['common.messageUnavailable'];
    }
    let template = typeof message === 'string' ? message : message[pluralCategory(selected.locale,Number(values[rule.plural]))]||message.other;
    // Corrupt/partial resources fail per message to canonical English. No blank
    // warning, raw key, injected markup, or exception becomes an operating state.
    let knownParameters=true;
    if(typeof template==='string')template.replace(/\{([a-z][a-zA-Z0-9]*)\}/g,(_match,name)=>{if(!rule.parameters.includes(name))knownParameters=false;return '';});
    const valid=typeof template==='string'&&template.trim()&&rule.parameters.every(name=>template.includes('{'+name+'}'))
      &&!/[{}]/.test(template.replace(/\{[a-z][a-zA-Z0-9]*\}/g,''))&&knownParameters;
    if(!valid){selected=english;const original=english.messages[key];template=typeof original==='string'?original:original[pluralCategory(fallback,Number(values[rule.plural]))]||original.other;}
    // A function replacement preserves literal $&, Unicode names, UTC, hashes and units.
    return template.replace(/\{([a-z][a-zA-Z0-9]*)\}/g, (_match,name) => String(values[name]));
  }
  return Object.freeze({locale, effectiveLocale:compatible(data.catalogs[locale]) ? locale : fallback, messageLocale:key=>catalogFor(key).locale,text});
}
