/* Classic-script adapter. No Intl, DOM convenience methods, modules or network catalog. */
var AeroScienceI18n = (function () {
  var translator = null, info = null;
  function text(key, fallback, values) {
    if (!translator) return fallback;
    return translator.text(key, values || {}, fallback);
  }
  function use(metadata) {
    var data = typeof AeroScienceLocaleData === 'undefined' ? null : AeroScienceLocaleData;
    var format = typeof AeroScienceLocaleFormat === 'undefined' ? null : AeroScienceLocaleFormat;
    var valid = !!(data && format && typeof format.createTranslator === 'function' && data.manifest && data.schema &&
      data.catalogs && data.catalogs['en-US'] && data.manifest.version === 1 && data.schema.version === 1 &&
      data.catalogs['en-US'].version === data.manifest.catalogVersion);
    var compatible = !!(valid && metadata && metadata.version === 1 && metadata.catalogVersion === data.manifest.catalogVersion &&
      data.manifest.locales.some(function (locale) { return locale.id === metadata.locale; }));
    translator = valid ? format.createTranslator(data, compatible ? metadata.locale : 'en-US') : null;
    var requested = compatible ? metadata.locale : 'en-US', name = requested;
    if (valid) data.manifest.locales.forEach(function (locale) { if (locale.id === requested) name = locale.name; });
    info = { requestedLocale: requested, effectiveLocale: translator ? translator.effectiveLocale : 'en-US', fallback: !compatible,
      message: !compatible ? text('toolbar.catalogFallback', 'English fallback: companion/catalog unavailable or incompatible.') : requested !== 'en-US' ?
        text('toolbar.catalogPreview', name + ' preview uses English.', {name:name}) : text('toolbar.catalogEnglish', 'Bundled English catalog active.') };
    return info;
  }
  function apply(root, metadata) {
    var state = use(metadata), nodes = root.querySelectorAll('[data-native-i18n]');
    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];
      node.textContent = text(node.getAttribute('data-native-i18n'), node.textContent);
    }
    var named = root.querySelectorAll('[data-native-i18n-aria-label]');
    for (var j = 0; j < named.length; j++) named[j].setAttribute('aria-label',text(named[j].getAttribute('data-native-i18n-aria-label'),named[j].getAttribute('aria-label')));
    var notice = root.querySelector('#as-language-detail'); if (notice) notice.textContent = state.message;
    root.setAttribute('lang', state.effectiveLocale); return state;
  }
  return {use:use, text:text, apply:apply,
    createTranslator:function(data,locale,options){return AeroScienceLocaleFormat.createTranslator(data,locale,options);},
    supportedLocale:function(value,manifest){return AeroScienceLocaleFormat.supportedLocale(value,manifest);}};
}());
