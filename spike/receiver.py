"""Counting receiver for the reliability spike. Records every call; /slow/* sleeps to simulate long work."""
import json, os, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SLOW = float(os.environ.get("SLOW_SECONDS", "25"))
calls, lock = [], threading.Lock()

class H(BaseHTTPRequestHandler):
    def _handle(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n: self.rfile.read(n)
        if self.path == "/_dump":
            with lock: body = json.dumps(calls).encode()
            self.send_response(200); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body); return
        if self.path == "/_reset":
            with lock: calls.clear()
            self.send_response(200); self.send_header("Content-Length", "0"); self.end_headers(); return
        rec = {"path": self.path, "start": time.time(), "done": None}
        with lock: calls.append(rec)
        if self.path.startswith("/slow/"): time.sleep(SLOW)
        rec["done"] = time.time()
        self.send_response(200); self.send_header("Content-Length", "0"); self.end_headers()
    do_GET = do_POST = do_PUT = _handle
    def log_message(self, *a): pass

class S(ThreadingHTTPServer):
    request_queue_size = 512   # default backlog is 5: a 200-callback burst gets connections refused
    daemon_threads = True

S(("0.0.0.0", 8080), H).serve_forever()
