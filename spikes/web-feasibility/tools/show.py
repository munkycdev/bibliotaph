import json, sys
t = sys.stdin.read(); d = json.loads(t[t.index("{"):])
print({k: d[k] for k in ("where", "books", "pages", "addMs", "stats")})
for s in d["searches"]:
    r = s.get("result", s)
    print(s["run"], r["query"], "docs", r["documents"], round(r["documentsMs"]), "ms | books", r["books"], "pages", r["matchingPages"], round(r["pagesMs"]), "ms")
