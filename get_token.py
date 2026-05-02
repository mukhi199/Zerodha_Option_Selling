import hashlib
import requests

api_key = "6zfzn8a4x91snnpz"
api_secret = "ax08xkr022fiawsy4le2dtewl1lsvszn"
req_token = "Cg0utZm5tCCCYrsP0JsomR9oeKQU4UJr"
chksum = hashlib.sha256((api_key + req_token + api_secret).encode('utf-8')).hexdigest()

data = {
    "api_key": api_key,
    "request_token": req_token,
    "checksum": chksum
}

r = requests.post("https://api.kite.trade/session/token", data=data)
if r.status_code == 200:
    res = r.json()
    token = res['data']['access_token']
    print("SUCCESS|" + token)
else:
    print("FAIL|" + r.text)
