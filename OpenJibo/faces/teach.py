"""Teach a face from the server, without speech: the person stands in front of Jibo, then run
    docker compose --profile self-hosted exec faces python /app/teach.py Makis
It takes a photo with Jibo's camera (JIBO_CAMERA_URL) and enrolls it. Run it 2-3 times per person,
turning the head a little each time. Also: teach.py --list, teach.py --forget Makis.
"""
import json
import sys
import urllib.request

CAMERA = (__import__("os").environ.get("JIBO_CAMERA_URL") or "").rstrip("/")
FACES = "http://127.0.0.1:8487"


def call(method, url, body=None, content_type="application/octet-stream"):
    request = urllib.request.Request(url, data=body, method=method)
    if body is not None:
        request.add_header("Content-Type", content_type)
    with urllib.request.urlopen(request, timeout=20) as response:
        return response.read()


def main(args):
    if args == ["--list"]:
        print(json.loads(call("GET", FACES + "/people"))["people"] or "nobody yet")
        return 0
    if len(args) == 2 and args[0] == "--forget":
        print(json.loads(call("DELETE", FACES + "/people?name=" + urllib.parse.quote(args[1]))))
        return 0
    if len(args) != 1:
        print(__doc__)
        return 1
    if not CAMERA:
        print("JIBO_CAMERA_URL is not set (OPENJIBO_JIBO_CAMERA_URL in .env).")
        return 1
    taken = json.loads(call("POST", CAMERA + "/media/photo", b'{"camera":1,"type":2,"filters":[]}', "application/json"))
    photo = call("GET", CAMERA + "/media/photo?id=" + urllib.parse.quote(taken["id"]))
    result = json.loads(call("POST", FACES + "/enroll?name=" + urllib.parse.quote(args[0]), photo))
    if result.get("ok"):
        print(f"Learned {result['name']} ({result['samples']} photo(s) so far).")
    elif result.get("reason") == "several_faces":
        print("More than one face in the photo: only one person in front of Jibo, please.")
    else:
        print("No face found: look straight at Jibo, a bit closer, with a light on, and try again.")
    return 0


if __name__ == "__main__":
    import urllib.parse  # noqa: E402
    sys.exit(main(sys.argv[1:]))
