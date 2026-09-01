from PIL import Image, ImageDraw, ImageFont

VS_PURPLE = '#68217A'
NEO_GREEN = '#57A143'
WHITE = '#FFFFFF'
DARK = '#1E1E1E'
SIZE = 90
FONT = r'C:\Windows\Fonts\consolab.ttf'  # Consolas Bold

def base(color=VS_PURPLE):
    img = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=14, fill=color)
    return img, d

def text_center(d, s, font_size, fill, dy=0):
    f = ImageFont.truetype(FONT, font_size)
    box = d.textbbox((0, 0), s, font=f)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text(((SIZE - w) / 2 - box[0], (SIZE - h) / 2 - box[1] + dy), s, font=f, fill=fill)

# v1: green "N" with a block caret tucked at the baseline right
img, d = base()
text_center(d, 'N', 58, NEO_GREEN, dy=-4)
d.rectangle([58, 62, 74, 74], fill=WHITE)
img.save(r'O:\tempproj\NeoVS\assets\icon_v1.png')

# v2: prompt + block caret, all green
img, d = base()
text_center(d, '>', 46, NEO_GREEN, dy=-10)
d.rectangle([44, 52, 72, 70], fill=NEO_GREEN)
img.save(r'O:\tempproj\NeoVS\assets\icon_v2.png')

# v3: white "N", green block caret
img, d = base()
text_center(d, 'N', 58, WHITE, dy=-4)
d.rectangle([58, 62, 74, 74], fill=NEO_GREEN)
img.save(r'O:\tempproj\NeoVS\assets\icon_v3.png')

# v4: dark background variant of v1
img, d = base(DARK)
text_center(d, 'N', 58, NEO_GREEN, dy=-4)
d.rectangle([58, 62, 74, 74], fill=WHITE)
img.save(r'O:\tempproj\NeoVS\assets\icon_v4.png')

# contact sheet for review
sheet = Image.new('RGBA', (SIZE * 4 + 30, SIZE), (40, 40, 40, 255))
for i in range(4):
    sheet.paste(Image.open(rf'O:\tempproj\NeoVS\assets\icon_v{i+1}.png'), (i * (SIZE + 10), 0))
sheet.save(r'O:\tempproj\NeoVS\assets\icon_sheet.png')
print('ok')
