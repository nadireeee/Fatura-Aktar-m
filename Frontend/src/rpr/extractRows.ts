type AnyObj = Record<string, any>;

const isObject = (v: any): v is AnyObj => v != null && typeof v === 'object' && !Array.isArray(v);

// DIA RPR: response shape variants we observed / expect:
// - array
// - { __rows: [] }
// - { data: [] } or { success:true, data: [] }
// - { result: "<base64>" } / "<base64>" / JSON string
export function extractRows(input: unknown): any[] {
  const tryParseJson = (s: string): any => {
    const t = String(s ?? '').trim();
    if (!t) return null;
    try {
      return JSON.parse(t);
    } catch {
      return null;
    }
  };

  const tryBase64ToJson = (s: string): any => {
    const t = String(s ?? '').trim();
    if (!t) return null;
    // base64 strings are usually long and contain no braces
    // if it looks like JSON, don't treat as base64
    if (t.startsWith('{') || t.startsWith('[')) return tryParseJson(t);
    try {
      const decoded = atob(t.replace(/\s+/g, ''));
      return tryParseJson(decoded);
    } catch {
      return null;
    }
  };

  const unwrap = (v: any): any => {
    if (Array.isArray(v)) return v;
    if (isObject(v)) {
      if (Array.isArray(v.__rows)) return v.__rows;
      if (Array.isArray(v.rows)) return v.rows;
      if (Array.isArray(v.data)) return v.data;
      if (isObject(v.data) && Array.isArray(v.data.__rows)) return v.data.__rows;
      if (isObject(v.result) && Array.isArray((v.result as any).__rows)) return (v.result as any).__rows;
      if (Array.isArray(v.result)) return v.result;
      if (typeof v.result === 'string') {
        const j = tryBase64ToJson(v.result);
        const u = unwrap(j);
        if (Array.isArray(u)) return u;
      }
      if (typeof (v as any) === 'string') {
        const j = tryParseJson(v as any);
        const u = unwrap(j);
        if (Array.isArray(u)) return u;
      }
      // backend sometimes returns { success:false, message:"..." }
      if (v.success === false) {
        const msg = String(v.message ?? 'Rapor çağrısı başarısız.');
        throw new Error(msg);
      }
    }
    if (typeof v === 'string') {
      const j = tryBase64ToJson(v);
      const u = unwrap(j);
      if (Array.isArray(u)) return u;
    }
    return null;
  };

  const rows = unwrap(input);
  if (rows == null) return [];
  if (!Array.isArray(rows)) return [];
  return rows;
}

