# Builds the Steam guide images from raw game screenshots in shots/.
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageEnhance
import sys, os
S = "shots"; O = "publish/images"
F = lambda size, bold=True: ImageFont.truetype("C:/Windows/Fonts/" + ("segoeuib.ttf" if bold else "segoeui.ttf"), size)
GREEN = (57, 255, 90)

def shadow_text(d, xy, text, font, fill=(255, 255, 255), anchor="la", off=3):
    d.text((xy[0] + off, xy[1] + off), text, font=font, fill=(0, 0, 0), anchor=anchor)
    d.text(xy, text, font=font, fill=fill, anchor=anchor)

def strip_overlay(img):
    # Hide the mod's own debug-ish status line at the top-left for hero shots.
    return img

game = Image.open(f"{S}/p_g_host.png").convert("RGB")
room = Image.open(f"{S}/p_room.png").convert("RGB")
menu = Image.open(f"{S}/p_menu.png").convert("RGB")

# 1) cover 1280x720
bg = ImageEnhance.Brightness(game.filter(ImageFilter.GaussianBlur(2))).enhance(0.55)
d = ImageDraw.Draw(bg)
grad = Image.new("L", (1280, 720)); gd = ImageDraw.Draw(grad)
for x in range(1280): gd.line([(x, 0), (x, 720)], fill=int(max(0, 220 - x * 0.35)))
bg = Image.composite(Image.new("RGB", (1280, 720), (8, 10, 12)), bg, grad)
d = ImageDraw.Draw(bg)
shadow_text(d, (70, 170), "VOXEL", F(110), fill=(255, 255, 255))
shadow_text(d, (70, 290), "COOP", F(150), fill=GREEN)
shadow_text(d, (76, 470), "Online multiplayer for Voxel Playground", F(36, False))
shadow_text(d, (76, 520), "Up to 4 players  •  Steam  •  No port forwarding", F(30, False), fill=(200, 255, 210))
d.rounded_rectangle([76, 590, 316, 646], 10, fill=GREEN)
d.text((196, 618), "FREE MOD", font=F(30), fill=(10, 20, 10), anchor="mm")
bg.save(f"{O}/cover.png")

# 2) square preview 512x512 (Steam guide thumbnail)
sq = game.crop((300, 90, 930, 720)).resize((512, 512), Image.LANCZOS)
sq = ImageEnhance.Brightness(sq).enhance(0.75)
d = ImageDraw.Draw(sq)
d.rectangle([0, 360, 512, 512], fill=(8, 10, 12))
shadow_text(d, (256, 405), "ONLINE CO-OP", F(54), fill=GREEN, anchor="mm")
shadow_text(d, (256, 465), "Voxel Playground • 4 players", F(28, False), anchor="mm")
sq.save(f"{O}/preview_square.png")

# 3) how-to strips (EN + RU)
def howto(labels, name):
    W, H = 1280, 420
    strip = Image.new("RGB", (W, H), (18, 20, 24)); d = ImageDraw.Draw(strip)
    crops = [menu.crop((420, 100, 1240, 600)), room.crop((380, 160, 900, 560)), game.crop((200, 100, 1080, 720))]
    pw = (W - 4 * 24) // 3
    for i, (img, label) in enumerate(zip(crops, labels)):
        x = 24 + i * (pw + 24)
        im = img.copy(); im.thumbnail((pw, 300))
        strip.paste(im, (x + (pw - im.width) // 2, 24 + (300 - im.height) // 2))
        d.ellipse([x, 340, x + 52, 392], fill=GREEN)
        d.text((x + 26, 366), str(i + 1), font=F(32), fill=(10, 20, 10), anchor="mm")
        d.text((x + 66, 366), label, font=F(28), fill=(255, 255, 255), anchor="lm")
    strip.save(f"{O}/{name}")
howto(["Host: Create Room", "Share the 5-letter code", "Friend: Join By Code"], "howto_en.png")
howto(["Хост: Create Room", "Скинь код друзьям", "Друг: Join By Code"], "howto_ru.png")

# 4) clean full shots for the gallery
menu.save(f"{O}/shot_menu.png"); room.save(f"{O}/shot_room.png"); game.save(f"{O}/shot_ingame.png")
Image.open(f"{S}/p_maps.png").convert("RGB").save(f"{O}/shot_sandbox.png")
Image.open(f"{S}/p_joined.png").convert("RGB").save(f"{O}/shot_client_lobby.png")
print(os.listdir(O))
