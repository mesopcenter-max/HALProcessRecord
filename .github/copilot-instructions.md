# Copilot Instructions

## Project Guidelines
- For this project, furnace selection must include only RRP_1 through RRP_5 and RRP_10 through RRP_19. Endpoint mapping is RRP_1=172.133.63.150, RRP_2=172.133.63.151, RRP_3=172.133.63.152, RRP_4=172.133.63.153, RRP_5=172.133.63.154, and RRP_10 through RRP_19 map sequentially to 172.133.63.156 through 172.133.63.165; RRP_6 through RRP_9 are excluded.

## Timing Logic
- For timing logic, each work order follows this sequence: furnace door opens, the work order is removed, the furnace door closes, the work order is placed in the machine, then the machine starts and stops.
- Transfer time must be measured from furnace door open to machine start even when the door is already closed.
- Use the DoorClosed falling edge (1→0) as the door-open event and the DoorClosed rising edge (0→1) as the close event. 
- When both DoorOpen and DoorClosed signals are false, repeated DoorClosed 0→1 transitions occurring more than once indicate half-door evidence rather than a normal close.
- For DoorClosed timing, retain the precise 1→0 opening edge when captured; if missed, infer open time from the last recorded DoorClosed=0 sample when the 0→1 close edge occurs, so transactions are not lost when only the close transition is observed.
- Enforce a maximum transfer time of 60 seconds and maximum total time of 210 seconds (3 minutes 30 seconds).
- For invalid historical rows, re-pair using the nearest valid door/start/stop signals rather than merely hiding the row.

## Operation History Display
- For operation history display, prioritize complete furnace-matched rows containing transfer, total, and operation times. Use machine-only operation rows only as a fallback when no furnace-matched rows are available.

## UI Styling
- When fixing styling, preserve and restore the user's existing CSS rather than inventing or redesigning the UI unless explicitly asked.