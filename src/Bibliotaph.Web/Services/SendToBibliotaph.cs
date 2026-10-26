namespace Bibliotaph.Web.Services;

/// <summary>
/// The Send to Bibliotaph bookmark: a javascript: link the user keeps in the browser's bookmarks bar and clicks on a
/// service's library page. It takes the page as it shows, as the Windows app's browser does (what is chosen in a list
/// or typed in a search box written in, never a password form), opens Bibliotaph's /online/receive and, once that tab
/// says it is ready, posts the page to it; then it fetches the covers the tab asks for, from the service's page, where
/// the browser may fetch them. A page whose site cuts it off from the tabs it opens gets a line saying what to do.
/// </summary>
public static class SendToBibliotaph
{
    const string Script = """
        (()=>{const B=ORIGIN;
        for(const l of document.querySelectorAll('select'))for(const o of l.options)o.toggleAttribute('selected',o.selected);
        for(const i of document.querySelectorAll('input')){const t=(i.getAttribute('type')||'text').toLowerCase();
        if(t=='checkbox'||t=='radio')i.toggleAttribute('checked',i.checked);
        else if((t=='text'||t=='search')&&!i.form?.querySelector('input[type=password]'))i.setAttribute('value',i.value)}
        const h=document.documentElement.outerHTML,u=location.href,w=window.open(B+'/online/receive');let s=0;
        const no=()=>alert('Bibliotaph couldn\'t take this page from here. Check you\'re signed in at '+B+'. Or use the Send to Bibliotaph extension, or save the page with Save as and import it in Settings, Online libraries.');
        if(!w||w.closed)return no();
        addEventListener('message',async e=>{if(e.origin!=B||e.source!=w)return;const d=e.data||{};
        if(d.bibliotaph=='ready'&&!s){s=1;w.postMessage({bibliotaph:'page',url:u,html:h},B)}
        else if(d.bibliotaph=='covers'){for(const c of d.urls||[]){try{const r=await fetch(c);
        if(r.ok){const b=await r.arrayBuffer();w.postMessage({bibliotaph:'cover',url:c,type:r.headers.get('content-type')||'',bytes:b},B,[b])}}catch{}}
        w.postMessage({bibliotaph:'covers-done'},B)}});
        setTimeout(()=>{if(!s)no()},20000)})()
        """;

    /// <summary>The bookmark's address, sending to Bibliotaph at <paramref name="origin"/> (https://bibliotaph.online).</summary>
    public static string Bookmarklet(string origin)
    {
        var script = string.Concat(Script.Split('\n').Select(line => line.Trim()))
            .Replace("ORIGIN", "'" + origin.TrimEnd('/').Replace("'", "", StringComparison.Ordinal) + "'", StringComparison.Ordinal);
        // Kept readable where it can be: only what an address can't hold is escaped.
        return "javascript:" + Uri.EscapeDataString(script).Replace("%28", "(").Replace("%29", ")").Replace("%27", "'")
            .Replace("%3D", "=").Replace("%2C", ",").Replace("%3B", ";").Replace("%3A", ":").Replace("%2F", "/").Replace("%2B", "+")
            .Replace("%21", "!").Replace("%3F", "?").Replace("%26", "&").Replace("%5B", "[").Replace("%5D", "]").Replace("%2A", "*");
    }
}
