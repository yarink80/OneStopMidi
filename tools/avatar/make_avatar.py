"""Generate the app's avatar images and Windows icon from a portrait photo.

Usage (from a venv with: pip install "rembg[cpu]" pillow scipy):
    python -I tools/avatar/make_avatar.py <photo> src/OneStopMidi.App/Assets [preview.png]

Writes elie-cutout.png (background removed), elie-avatar.png (512 px round badge) and OneStopMidi.ico (16-256 px).
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from rembg import new_session, remove
from scipy import ndimage

src, out_dir = Path(sys.argv[1]), Path(sys.argv[2])
preview_path = Path(sys.argv[3]) if len(sys.argv) > 3 else None
out_dir.mkdir(parents=True, exist_ok=True)

photo = Image.open(src).convert("RGB")
cut = remove(photo, session=new_session("birefnet-portrait"))

# Keep only the connected region that contains the face, dropping bystanders and banner fragments.
face_x, face_y = 195, 240
rgba = np.array(cut)
labels, _ = ndimage.label(rgba[:, :, 3] > 40)
face_label = labels[face_y, face_x]
if face_label:
    keep = ndimage.binary_dilation(labels == face_label, iterations=2)
    rgba[:, :, 3] = rgba[:, :, 3] * keep
cut = Image.fromarray(rgba)
cut.save(out_dir / "elie-cutout.png")

# Square crop centred on the face, padded with transparency where it overhangs the photo.
cx, cy, side = 195, 268, 540
crop = Image.new("RGBA", (side, side), (0, 0, 0, 0))
crop.paste(cut, (side // 2 - cx, side // 2 - cy), cut)


def avatar(size: int) -> Image.Image:
    """Round badge: blue-violet gradient disc, cut-out face, thin light ring."""
    s = size * 4  # supersample for smooth edges
    grad = Image.new("RGBA", (s, s))
    top, bottom = (88, 101, 242), (32, 36, 64)
    draw = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        draw.line([(0, y), (s, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    grad.alpha_composite(crop.resize((s, s), Image.LANCZOS))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse([0, 0, s - 1, s - 1], fill=255)
    disc = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    disc.paste(grad, (0, 0), mask)
    ring = max(2, s // 40)
    ImageDraw.Draw(disc).ellipse([ring // 2, ring // 2, s - 1 - ring // 2, s - 1 - ring // 2],
                                 outline=(230, 233, 255, 255), width=ring)
    return disc.resize((size, size), Image.LANCZOS)


big = avatar(512)
big.save(out_dir / "elie-avatar.png")
avatar(256).save(out_dir / "OneStopMidi.ico", sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)],
                 append_images=[avatar(n) for n in (16, 24, 32, 48, 64, 128)])

if preview_path:
    # Cut-out on a checkerboard next to the badge, to eyeball the background removal.
    checker = Image.new("RGBA", cut.size, (255, 255, 255, 255))
    d = ImageDraw.Draw(checker)
    for y in range(0, cut.size[1], 16):
        for x in range(0, cut.size[0], 16):
            if (x // 16 + y // 16) % 2:
                d.rectangle([x, y, x + 15, y + 15], fill=(204, 204, 204, 255))
    checker.alpha_composite(cut)
    preview = Image.new("RGBA", (cut.size[0] + 532, max(cut.size[1], 512)), (40, 40, 48, 255))
    preview.paste(checker, (0, 0))
    preview.alpha_composite(big, (cut.size[0] + 10, 0))
    preview.convert("RGB").save(preview_path)
print("done")
