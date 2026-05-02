import requests
import urllib.parse
msg = "Test"
url = f"https://api.telegram.org/bot8507322615:AAEH51VrBe-lRUvaA8VM1_Q1RT4y7-9nuk0/sendMessage?chat_id=671099433&text={urllib.parse.quote(msg)}"
r = requests.get(url)
print(r.status_code, r.text)
