#!/usr/bin/env bash
# Walks one order through the whole system over HTTP: courier, order (with listing photos), buyer confirms the
# photos (which starts payment), payment, pickup, delivery, the buyer's code, payouts, settlement, reconciliation.
#
#   docker compose up -d
#   dotnet run --project src/CourierEscrow.Api     # Development: mock provider, dev keys, schema created on start
#   ./scripts/demo.sh
#
# Needs curl and python3. Reads the buyer's code from the API's log output when it is run from the same machine:
#   LOG=/path/to/api.log ./scripts/demo.sh   (otherwise it asks you to type the code from the log)
# End-to-end run over HTTP against the API. Needs: curl, python3.
API=${API:-http://localhost:5080}
ADMIN="X-Api-Key: dev-admin-key"
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1]))" "$1"; }
step() { echo; echo "== $1"; }
post() { curl -s -X POST "$API$1" -H "$ADMIN" -H 'Content-Type: application/json' -d "${2:-{\}}"; }

step "no key -> 401"; curl -s -o /dev/null -w "%{http_code}\n" $API/admin/orders
step "create courier"
COURIER=$(post /admin/couriers '{"name":"Mphatso Banda","phone":"0999000111","network":"Airtel","pin":"4821"}' | j "d['id']"); echo $COURIER

step "upload the 3 listing photos (staff)"
lphoto() { # staff presign + upload one listing photo, print id
  S=$(post /admin/uploads/presign '{"contentType":"image/jpeg"}')
  U=$(echo "$S" | j "d['url']"); ID=$(echo "$S" | j "d['uploadId']")
  head -c 2000 /dev/urandom | curl -s -o /dev/null -X PUT "$U" --data-binary @- ; echo $ID; }
L1=$(lphoto); L2=$(lphoto); L3=$(lphoto); echo "$L1 $L2 $L3"

step "create order"
ORDER=$(post /admin/orders "{\"buyerName\":\"Buyer Banda\",\"buyerPhone\":\"0888111222\",\"buyerNetwork\":\"Tnm\",\"sellerName\":\"Chikondi Phones\",\"sellerPhone\":\"0991234567\",\"sellerNetwork\":\"Airtel\",\"courierId\":\"$COURIER\",\"itemDescription\":\"Samsung A15\",\"itemAmount\":30000000,\"deliveryFee\":800000,\"pickupAddress\":\"Area 25 market\",\"deliveryAddress\":\"Kawale\",\"listingUploadIds\":[\"$L1\",\"$L2\",\"$L3\"]}")
OID=$(echo "$ORDER" | j "d['id']"); echo "$ORDER" | j "(d['orderNumber'], d['total'], d['buyerPhone'])"
step "payment before the buyer confirms the photos -> refused"; post /admin/orders/$OID/payment | j "d"
step "buyer confirms the photos on the tracking link (this starts payment)"
TT=$(echo "$ORDER" | j "d['trackingToken']"); curl -s -X POST $API/track/$TT/confirm-product | j "d"
step "buyer pays (dev webhook)"; curl -s -X POST $API/dev/pay/$OID | j "d"
step "courier signs in"
TOKEN=$(curl -s -X POST $API/courier/login -H 'Content-Type: application/json' -d '{"phone":"+265 999 000 111","pin":"4821","deviceId":"phone-1"}' | j "d['token']")
C="Authorization: Bearer $TOKEN"
curl -s $API/courier/jobs -H "$C" | j "[(x['orderNumber'],x['state'],x['nextAction'],len(x['listingPhotoIds'])) for x in d]"
step "courier admin endpoint -> 403"; curl -s -o /dev/null -w "%{http_code}\n" $API/admin/orders -H "$C"
photo() { # presign + upload one photo, print id
  S=$(curl -s -X POST $API/courier/uploads/presign -H "$C" -H 'Content-Type: application/json' -d '{"contentType":"image/jpeg"}')
  U=$(echo "$S" | j "d['url']"); ID=$(echo "$S" | j "d['uploadId']")
  head -c 2000 /dev/urandom | curl -s -o /dev/null -X PUT "$U" --data-binary @- ; echo $ID; }
P1=$(photo); P2=$(photo); P3=$(photo)
step "pickup (2 photos -> 422, then 3)"
ev() { curl -s -w "\n%{http_code}\n" -X POST $API/courier/orders/$OID/events -H "$C" -H 'Content-Type: application/json' -d "$1"; }
ev "{\"type\":\"pickup\",\"clientEventId\":\"e0\",\"uploadIds\":[\"$P1\",\"$P2\"],\"weightKg\":0.6,\"sealId\":\"SL-558201\",\"latitude\":-13.97,\"longitude\":33.78}" | tail -1
ev "{\"type\":\"pickup\",\"clientEventId\":\"e1\",\"uploadIds\":[\"$P1\",\"$P2\",\"$P3\"],\"weightKg\":0.6,\"sealId\":\"SL-558201\",\"latitude\":-13.97,\"longitude\":33.78}"
step "retry same event -> replayed"; ev "{\"type\":\"pickup\",\"clientEventId\":\"e1\",\"uploadIds\":[\"$P1\",\"$P2\",\"$P3\"],\"weightKg\":0.6,\"sealId\":\"SL-558201\",\"latitude\":-13.97,\"longitude\":33.78}" | head -1
step "arrive"; P4=$(photo)
ev "{\"type\":\"arrived\",\"clientEventId\":\"e2\",\"uploadIds\":[\"$P4\"],\"latitude\":-13.98,\"longitude\":33.79}" | head -1
step "wrong OTP"; ev '{"type":"otp","clientEventId":"e3","otp":"000000"}'
sleep 3
OTP=""
if [ -n "$LOG" ] && [ -f "$LOG" ]; then OTP=$(grep -o 'Give this code ONLY after you inspect and accept it: [0-9]* [0-9]*' "$LOG" | tail -1 | sed 's/.*: //;s/ //'); fi
if [ -z "$OTP" ]; then read -r -p "Type the buyer's 6-digit code from the API log (SMS to the buyer): " OTP; fi
step "right OTP ($OTP)"; ev "{\"type\":\"otp\",\"clientEventId\":\"e4\",\"otp\":\"$OTP\"}"
sleep 8
step "order detail"; curl -s $API/admin/orders/$OID -H "$ADMIN" | j "(d['order']['state'], 'held', d['heldInEscrow'], [(p['kind'],p['amount'],p['status']) for p in d['payouts']])"
step "evidence on the order (listing + pickup + arrival)"; curl -s $API/admin/orders/$OID -H "$ADMIN" | j "sorted(p['kind'] for p in d['proofs'])"
step "confirm payouts via dev webhook"
for REF in $(curl -s $API/admin/payouts -H "$ADMIN" | j "' '.join(p['reference'] for p in d)"); do
  AMT=$(curl -s $API/admin/payouts -H "$ADMIN" | j "[p['amount'] for p in d if p['reference']=='$REF'][0]"); curl -s -X POST "$API/dev/payout-success/$REF?amount=$AMT" | j "d"; done
step "final"; curl -s $API/admin/orders/$OID -H "$ADMIN" | j "(d['order']['state'], d['heldInEscrow'])"
curl -s $API/admin/summary -H "$ADMIN" | j "d"
step "reconciliation + tracking"; post "/admin/reconciliation/run" | j "d['run']"
curl -s $API/admin/alerts -H "$ADMIN" | j "d"
