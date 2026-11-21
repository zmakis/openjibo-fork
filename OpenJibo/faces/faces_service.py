"""OpenJibo local face recogniser.

A small HTTP service that runs next to the OpenJibo server on a home network. It learns the
faces of the people in the house from photos taken with Jibo's camera and tells the server
who is in a new photo. Everything stays on this machine: only face "embeddings" (lists of
numbers) are stored, in FACES_DATA (default /data/faces.json).

Models: OpenCV Zoo YuNet (face detection) and SFace (face recognition).

API (all bodies are raw JPEG/PNG bytes):
  POST /enroll?name=Makis   learn one face from the photo (the biggest face in it)
  POST /identify            -> {"faces": 2, "names": ["Makis"]}
  DELETE /people?name=Makis forget a person
  GET  /people              -> {"people": {"Makis": 3}}
  GET  /health
"""
import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import cv2
import numpy as np

DATA_PATH = os.environ.get("FACES_DATA", "/data/faces.json")
DETECT_MODEL = os.environ.get("FACES_DETECT_MODEL", "/models/face_detection_yunet.onnx")
RECOGNIZE_MODEL = os.environ.get("FACES_RECOGNIZE_MODEL", "/models/face_recognition_sface.onnx")
PORT = int(os.environ.get("FACES_PORT", "8487"))
# SFace cosine similarity: same person usually above ~0.36. Raise to be stricter.
MATCH_THRESHOLD = float(os.environ.get("FACES_MATCH_THRESHOLD", "0.40"))
MAX_SAMPLES_PER_PERSON = 20
MAX_BODY_BYTES = 15 * 1024 * 1024

_lock = threading.Lock()
_detector = cv2.FaceDetectorYN.create(DETECT_MODEL, "", (320, 320), 0.8, 0.3, 5000)
_recognizer = cv2.FaceRecognizerSF.create(RECOGNIZE_MODEL, "")


def load_people():
    try:
        with open(DATA_PATH, "r", encoding="utf-8") as handle:
            raw = json.load(handle)
        return {name: [np.array(v, dtype=np.float32) for v in vectors] for name, vectors in raw.items()}
    except FileNotFoundError:
        return {}


def save_people(people):
    os.makedirs(os.path.dirname(DATA_PATH) or ".", exist_ok=True)
    tmp = DATA_PATH + ".new"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump({name: [v.tolist() for v in vectors] for name, vectors in people.items()}, handle)
    os.replace(tmp, DATA_PATH)


_people = load_people()


def decode_image(body):
    image = cv2.imdecode(np.frombuffer(body, np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("not an image")
    return image


def detect_faces(image):
    """Faces sorted biggest first (YuNet rows: x, y, w, h, landmarks..., score)."""
    height, width = image.shape[:2]
    with _lock:
        _detector.setInputSize((width, height))
        _, faces = _detector.detect(image)
    if faces is None:
        return []
    return sorted(faces, key=lambda f: f[2] * f[3], reverse=True)


def embed(image, face):
    with _lock:
        aligned = _recognizer.alignCrop(image, face)
        return _recognizer.feature(aligned).flatten().astype(np.float32)


def cosine(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-9))


def best_match(vector):
    best_name, best_score = None, -1.0
    for name, samples in _people.items():
        for sample in samples:
            score = cosine(vector, sample)
            if score > best_score:
                best_name, best_score = name, score
    return (best_name, best_score) if best_score >= MATCH_THRESHOLD else (None, best_score)


def clean_name(raw):
    name = " ".join((raw or "").split())[:40]
    return name if name and all(c.isalpha() or c in " '-" for c in name) else None


class Handler(BaseHTTPRequestHandler):
    server_version = "OpenJiboFaces/1.0"

    def _send(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_body(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_BODY_BYTES:
            raise ValueError("missing or too large body")
        return self.rfile.read(length)

    def _query(self):
        parsed = urlparse(self.path)
        return parsed.path, {k: v[0] for k, v in parse_qs(parsed.query).items()}

    def log_message(self, fmt, *args):  # quiet: no photos or names in access logs
        pass

    def do_GET(self):
        path, _ = self._query()
        if path == "/health":
            return self._send(200, {"ok": True, "people": len(_people)})
        if path == "/people":
            return self._send(200, {"people": {name: len(v) for name, v in _people.items()}})
        return self._send(404, {"error": "not found"})

    def do_POST(self):
        path, query = self._query()
        try:
            image = decode_image(self._read_body())
        except ValueError as error:
            return self._send(400, {"error": str(error)})

        faces = detect_faces(image)
        if path == "/identify":
            names = []
            for face in faces[:6]:
                name, _ = best_match(embed(image, face))
                if name and name not in names:
                    names.append(name)
            return self._send(200, {"faces": len(faces), "names": names})

        if path == "/enroll":
            name = clean_name(query.get("name"))
            if not name:
                return self._send(400, {"error": "bad name"})
            if not faces:
                return self._send(200, {"ok": False, "reason": "no_face"})
            if len(faces) > 1 and faces[1][2] * faces[1][3] > 0.5 * faces[0][2] * faces[0][3]:
                return self._send(200, {"ok": False, "reason": "several_faces"})
            vector = embed(image, faces[0])
            with _lock:
                samples = _people.setdefault(name, [])
                samples.append(vector)
                del samples[:-MAX_SAMPLES_PER_PERSON]
                save_people(_people)
                count = len(samples)
            return self._send(200, {"ok": True, "name": name, "samples": count})

        return self._send(404, {"error": "not found"})

    def do_DELETE(self):
        path, query = self._query()
        if path != "/people":
            return self._send(404, {"error": "not found"})
        name = clean_name(query.get("name"))
        with _lock:
            match = next((n for n in _people if name and n.lower() == name.lower()), None)
            if match:
                del _people[match]
                save_people(_people)
        return self._send(200, {"ok": match is not None, "name": match})


if __name__ == "__main__":
    print(f"OpenJibo faces listening on :{PORT} with {len(_people)} people", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
