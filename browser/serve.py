"""Serve browser/kns.html and browser/kns-studio.html and proxy the game gateway to the local KnS server, on one origin.

    python browser/serve.py [web_port] [server_port]

The page posts its encrypted envelope to /1mhero/gateway.php; this forwards it to the KnS
server (server-dotnet, local launcher port 8081) so the browser sees one origin and no CORS setup.
kns-studio.html uses /studio-api/* for the same-origin admin proxy. /studio serves the Studio as the
packaged server does, with /api/* proxied like the server's <base>api/. Bound to 127.0.0.1 only.
"""
import re
import sys
import urllib.error
import urllib.request
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

BROWSER_DIR = Path(__file__).resolve().parent
WEB_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
SERVER = f"http://127.0.0.1:{int(sys.argv[2]) if len(sys.argv) > 2 else 8081}"
FORWARDED_HEADERS = (
    "Content-Type", "X-Admin-Key", "Authorization",
    "USERNO", "SESSIONKEY", "CLIENTVERSION", "CLIENTBUNDLEIDENTIFIER", "CLIENTLANGUAGE",
    "TIMESTAMP", "TIMESIGNATURE",
)


class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(BROWSER_DIR), **kwargs)

    def do_GET(self):
        self._route(head=False)

    def do_HEAD(self):
        self._route(head=True)

    def _send_file(self, raw, content_type, cache, head):
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", cache)
        self.end_headers()
        if not head:
            self.wfile.write(raw)

    def _route(self, head):
        serve = super().do_HEAD if head else super().do_GET
        if self.path.split("?", 1)[0] in ("/", "/index.html", "/kns.html"):
            self.path = "/kns.html"
            serve()
        elif self.path.split("?", 1)[0] in ("/kns-studio.html", "/studio"):
            self.path = "/kns-studio.html"
            serve()
        elif re.fullmatch(r"/kns_data/\d+\.js", self.path):
            serve()
        elif re.fullmatch(r"/studio-assets/[a-f0-9]{16}/[a-f0-9]{64}\.png", self.path):
            path = BROWSER_DIR.parent / "work" / self.path.lstrip("/")
            if not path.is_file():
                self.send_error(404)
                return
            self._send_file(path.read_bytes(), "image/png", "public, max-age=31536000, immutable", head)
        elif self.path.startswith("/studio-api/") and not head:
            self._proxy("GET", self.path[len("/studio-api"):])
        elif self.path.startswith("/api/") and not head:
            self._proxy("GET", self.path[len("/api"):])
        else:
            self.send_error(404)

    def end_headers(self):
        if self.path.split("?", 1)[0] in ("/kns.html", "/kns-studio.html"):
            self.send_header("Cache-Control", "no-cache")
        super().end_headers()

    def do_POST(self):
        if self.path == "/1mhero/gateway.php":
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length)
            self._proxy("POST", "/1mhero/gateway.php", body)
            return
        for prefix in ("/studio-api/", "/api/"):
            if self.path.startswith(prefix):
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length) if length else b""
                self._proxy("POST", self.path[len(prefix) - 1:], body)
                return
        self.send_error(404)

    def _proxy(self, method, suffix, body=None):
        headers = {}
        for name in FORWARDED_HEADERS:
            value = self.headers.get(name)
            if value:
                headers[name] = value
        req = urllib.request.Request(
            SERVER + suffix,
            data=body if method == "POST" else None,
            headers=headers,
            method=method,
        )
        try:
            with urllib.request.urlopen(req, timeout=20) as res:
                status, reply = res.status, res.read()
                reply_type = res.headers.get("Content-Type", "application/json")
        except urllib.error.HTTPError as e:
            status, reply = e.code, e.read()
            reply_type = (e.headers.get("Content-Type") if e.headers else None) or "application/json"
        except OSError:
            self.send_error(502, "KnS server not reachable. Is the backend running on 8081?")
            return
        self.send_response(status)
        self.send_header("Content-Type", reply_type)
        self.send_header("Content-Length", str(len(reply)))
        self.end_headers()
        self.wfile.write(reply)

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", WEB_PORT), Handler)
    print(
        f"kns: http://127.0.0.1:{WEB_PORT}/kns.html  ->  gateway {SERVER}/1mhero/gateway.php; "
        f"studio http://127.0.0.1:{WEB_PORT}/kns-studio.html -> {SERVER}/admin/accounts",
        flush=True,
    )
    server.serve_forever()
