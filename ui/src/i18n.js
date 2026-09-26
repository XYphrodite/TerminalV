import ru from "./locales/ru.json" with { type: "json" };
import en from "./locales/en.json" with { type: "json" };

const dictionaries = { ru, en };
let currentLang = "ru";
let dict = ru;

function normalize(lang) {
  return lang === "en" ? "en" : "ru";
}

export function init(language) {
  const lang = normalize(language);
  currentLang = lang;
  dict = dictionaries[lang] || ru;
  try {
    document.documentElement.lang = lang;
  } catch {}
  return lang;
}

export function t(key, params) {
  let value = dict[key];
  if (value === undefined) {
    const fallback = dictionaries.ru[key];
    if (fallback !== undefined) value = fallback;
    else return key;
  }
  if (params && typeof params === "object") {
    for (const [k, v] of Object.entries(params)) {
      value = value.split(`{${k}}`).join(String(v));
    }
  }
  return value;
}

export function getLanguage() {
  return currentLang;
}
