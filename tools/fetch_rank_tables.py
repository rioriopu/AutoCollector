# -*- coding: utf-8 -*-
import urllib.request, io, re, json
from bs4 import BeautifulSoup

urls = {
 '暁月': 'https://itumononeko.com/ff14-00070/',
 '漆黒': 'https://itumononeko.com/ff14-jet-black-fate-trader/',
}
out = {}
for name, url in urls.items():
    req = urllib.request.Request(url, headers={'User-Agent':'Mozilla/5.0'})
    html = urllib.request.urlopen(req, timeout=30).read().decode('utf-8', 'replace')
    soup = BeautifulSoup(html, 'lxml')
    tables = soup.find_all('table')
    print(f'=== {name} : table {len(tables)} 個 ===')
    out[name] = []
    for ti, t in enumerate(tables):
        rows = []
        for tr in t.find_all('tr'):
            cells = [c.get_text(' ', strip=True) for c in tr.find_all(['th','td'])]
            if cells: rows.append(cells)
        if rows:
            out[name].append(rows)
            print(f'  [table{ti}] {len(rows)}行  ヘッダ={rows[0]}')
    # 見出しも拾う
    heads = [h.get_text(' ',strip=True) for h in soup.find_all(['h2','h3','h4'])]
    print(f'  見出し: {heads[:25]}')
    print()
json.dump(out, io.open('site_tables.json','w',encoding='utf-8'), ensure_ascii=False, indent=1)
print('→ site_tables.json に保存')
