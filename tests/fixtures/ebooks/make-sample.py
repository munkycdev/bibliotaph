"""Writes sample.epub, the made-up book the Kindle fixtures were converted from (see README.md)."""
import struct
import sys
import zipfile
import zlib


def png(width, height, rgb):
    raw = b''.join(b'\x00' + bytes(rgb) * width for _ in range(height))

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)

    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b''))


def chapter(title, body):
    return (f'<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>{title}</title>'
            f'<link rel="stylesheet" href="style.css" type="text/css"/></head><body><h1 id="top">{title}</h1>{body}</body></html>')


road = ''.join(f'<p>Mile {i} of the road ran on past fields and fences, low walls and old stones, under a sky that kept its '
               'grey all day while the carts went by and the dust settled behind them.</p>' for i in range(1, 25))

with zipfile.ZipFile(sys.argv[1] if len(sys.argv) > 1 else 'sample.epub', 'w') as z:
    z.writestr(zipfile.ZipInfo('mimetype'), 'application/epub+zip')
    z.writestr('META-INF/container.xml', '<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
               '<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
    z.writestr('OEBPS/content.opf', '<?xml version="1.0" encoding="utf-8"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">'
               '<metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">'
               '<dc:identifier id="id" opf:scheme="ISBN">9780306406157</dc:identifier><dc:title>The Sample Road</dc:title>'
               '<dc:creator opf:role="aut">Ann Writer</dc:creator><dc:creator opf:role="aut">Bo Second</dc:creator>'
               '<dc:publisher>Sample Press</dc:publisher><dc:subject>Fantasy</dc:subject>'
               '<dc:description>A short walk for testing, levels 1-3.</dc:description><dc:date>2019-05-14</dc:date>'
               '<dc:language>en</dc:language><meta name="cover" content="cover"/></metadata><manifest>'
               '<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="css" href="style.css" media-type="text/css"/>'
               '<item id="cover" href="cover.png" media-type="image/png"/><item id="map" href="map.png" media-type="image/png"/>'
               '<item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="c2.xhtml" media-type="application/xhtml+xml"/>'
               '<item id="c3" href="c3.xhtml" media-type="application/xhtml+xml"/></manifest>'
               '<spine toc="ncx"><itemref idref="c1"/><itemref idref="c2"/><itemref idref="c3"/></spine></package>')
    z.writestr('OEBPS/toc.ncx', '<?xml version="1.0" encoding="utf-8"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head></head>'
               '<docTitle><text>The Sample Road</text></docTitle><navMap>'
               '<navPoint id="n1" playOrder="1"><navLabel><text>Arrival</text></navLabel><content src="c1.xhtml"/></navPoint>'
               '<navPoint id="n2" playOrder="2"><navLabel><text>The Long Road</text></navLabel><content src="c2.xhtml"/>'
               '<navPoint id="n2a" playOrder="3"><navLabel><text>The Crossing</text></navLabel><content src="c2.xhtml#crossing"/></navPoint></navPoint>'
               '<navPoint id="n3" playOrder="4"><navLabel><text>The Lighthouse</text></navLabel><content src="c3.xhtml"/></navPoint></navMap></ncx>')
    z.writestr('OEBPS/style.css', 'h1{color:#335} .map{width:50%} p{margin:0 0 .5em}')
    z.writestr('OEBPS/cover.png', png(60, 90, (200, 40, 40)))
    z.writestr('OEBPS/map.png', png(40, 30, (40, 160, 40)))
    z.writestr('OEBPS/c1.xhtml', chapter('Arrival', '<p>The travellers reached the village at dusk and asked for the road north. '
               'See <a href="c3.xhtml#lamp">the lantern</a>.</p><p><img class="map" src="map.png" alt="A map of the village"/></p>'))
    # The heading lands inside a paragraph, as markup in real books sometimes does.
    z.writestr('OEBPS/c2.xhtml', chapter('The Long Road', road[:2000] + '<h2 id="crossing">The Crossing</h2>' + road[2000:]))
    z.writestr('OEBPS/c3.xhtml', chapter('The Lighthouse', '<p id="lamp">At the end of the road the keeper lit the lantern and watched the sea. '
               'Café — naïve “quotes”.</p><p><a href="c1.xhtml">Back to the start</a> or <a href="https://example.com/">a web page</a>.</p>'))
