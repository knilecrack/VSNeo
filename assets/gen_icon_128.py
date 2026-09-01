from PIL import Image, ImageDraw, ImageFont

VS_PURPLE = '#68217A'
NEO_GREEN = '#57A143'
SIZE = 128
FONT = r'C:\Windows\Fonts\consolab.ttf'  # Consolas Bold

img = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=20, fill=VS_PURPLE)

# Same proportions as the 90px v2: ">" at 46/90, caret block 28x18 at (44,52).
f = ImageFont.truetype(FONT, int(46 * SIZE / 90))
s = '>'
box = d.textbbox((0, 0), s, font=f)
w, h = box[2] - box[0], box[3] - box[1]
d.text(((SIZE - w) / 2 - box[0], (SIZE - h) / 2 - box[1] + int(-10 * SIZE / 90)),
       s, font=f, fill=NEO_GREEN)

d.rectangle([int(44 * SIZE / 90), int(52 * SIZE / 90),
             int(72 * SIZE / 90), int(70 * SIZE / 90)], fill=NEO_GREEN)

img.save(r'O:\tempproj\NeoVS\assets\icon_128.png', dpi=(96, 96))
print('ok', img.size, img.info.get('dpi'))
