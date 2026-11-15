from __future__ import annotations

import hashlib
import json
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


PROJECT_ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = PROJECT_ROOT / "src" / "HuiTian.App" / "Assets"
ACTIVE_PNG = ASSET_DIR / "HuiTianLogo.png"
ACTIVE_ICO = ASSET_DIR / "HuiTianLogo.ico"
VALIDATION_DIR = PROJECT_ROOT / "deliverables" / "icon-validation"
ORIGINAL_PNG = VALIDATION_DIR / "HuiTianLogo.original.png"
FRAME_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
ALPHA_CONTENT_THRESHOLD = 1
CONTENT_PADDING_RATIO = 0.0213
OPTICALLY_SIZED_FRAME_MAX = 64
OPTICAL_OVERSCAN_RATIO = 1.0


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def alpha_bbox(image: Image.Image, threshold: int) -> tuple[int, int, int, int] | None:
    alpha = image.getchannel("A")
    mask = alpha.point(lambda value: 255 if value > threshold else 0)
    return mask.getbbox()


def make_cropped_master(source: Image.Image) -> tuple[Image.Image, tuple[int, int, int, int]]:
    source = source.convert("RGBA")
    content = alpha_bbox(source, ALPHA_CONTENT_THRESHOLD)
    nonzero = alpha_bbox(source, 0)
    if content is None or nonzero is None:
        raise RuntimeError("The source logo has no visible pixels.")

    left, top, right, bottom = content
    content_width = right - left
    content_height = bottom - top
    padding = round(max(content_width, content_height) * CONTENT_PADDING_RATIO)
    side = max(content_width, content_height) + 2 * padding
    center_x = (left + right) / 2
    center_y = (top + bottom) / 2
    crop_left = round(center_x - side / 2)
    crop_top = round(center_y - side / 2)
    crop_box = (crop_left, crop_top, crop_left + side, crop_top + side)

    nz_left, nz_top, nz_right, nz_bottom = nonzero
    if not (
        crop_box[0] <= nz_left
        and crop_box[1] <= nz_top
        and crop_box[2] >= nz_right
        and crop_box[3] >= nz_bottom
    ):
        raise RuntimeError(
            f"Calculated crop {crop_box} would remove non-transparent source pixels {nonzero}."
        )

    return source.crop(crop_box), crop_box


def save_multisize_ico(master: Image.Image) -> list[Image.Image]:
    visible = alpha_bbox(master, 0)
    if visible is None:
        raise RuntimeError("The cropped logo has no visible pixels.")
    visible_master = master.crop(visible)
    frames: list[Image.Image] = []
    for size in FRAME_SIZES:
        if size <= OPTICALLY_SIZED_FRAME_MAX:
            # Windows taskbar icons are visually compared by their opaque
            # footprint, not by the ICO canvas. A small optical correction
            # removes the remaining translucent fringe and compensates for the
            # source artwork being slightly wider than it is tall.
            optical_size = max(size, round(size * OPTICAL_OVERSCAN_RATIO))
            optical = visible_master.resize(
                (optical_size, optical_size),
                Image.Resampling.LANCZOS,
            )
            left = (optical_size - size) // 2
            top = (optical_size - size) // 2
            frame = optical.crop((left, top, left + size, top + size))
        else:
            frame = master.resize((size, size), Image.Resampling.LANCZOS)
        frames.append(frame)
    frame_by_size = {frame.width: frame for frame in frames}
    primary = frame_by_size[max(FRAME_SIZES)]
    append_images = [frame_by_size[size] for size in FRAME_SIZES if size != primary.width]
    primary.save(
        ACTIVE_ICO,
        format="ICO",
        sizes=[(size, size) for size in FRAME_SIZES],
        append_images=append_images,
    )
    return frames


def ico_directory(path: Path) -> list[dict[str, int | str]]:
    data = path.read_bytes()
    reserved, image_type, count = struct.unpack_from("<HHH", data, 0)
    if reserved != 0 or image_type != 1:
        raise RuntimeError("Generated file is not a valid ICO container.")

    entries: list[dict[str, int | str]] = []
    for index in range(count):
        offset = 6 + index * 16
        width, height, colors, _, planes, bits, byte_count, image_offset = struct.unpack_from(
            "<BBBBHHII", data, offset
        )
        width = width or 256
        height = height or 256
        signature = data[image_offset : image_offset + 8]
        encoding = "PNG" if signature == b"\x89PNG\r\n\x1a\n" else "DIB"
        entries.append(
            {
                "index": index,
                "width": width,
                "height": height,
                "color_count": colors,
                "planes": planes,
                "bits_per_pixel": bits,
                "encoding": encoding,
                "bytes": byte_count,
                "offset": image_offset,
            }
        )
    return entries


def load_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    names = ["segoeuib.ttf" if bold else "segoeui.ttf", "arialbd.ttf" if bold else "arial.ttf"]
    for name in names:
        candidate = Path("C:/Windows/Fonts") / name
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size)
    return ImageFont.load_default()


def checkerboard(size: int, tile: int = 8) -> Image.Image:
    image = Image.new("RGBA", (size, size), (238, 240, 244, 255))
    draw = ImageDraw.Draw(image)
    for y in range(0, size, tile):
        for x in range(0, size, tile):
            if (x // tile + y // tile) % 2:
                draw.rectangle((x, y, x + tile - 1, y + tile - 1), fill=(207, 211, 219, 255))
    return image


def make_contact_sheet(frames: list[Image.Image], frame_stats: list[dict[str, object]]) -> Path:
    columns = 3
    cell_width, cell_height = 420, 330
    header_height = 90
    rows = (len(frames) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * cell_width, header_height + rows * cell_height), "#F4F6FA")
    draw = ImageDraw.Draw(sheet)
    title_font = load_font(30, bold=True)
    label_font = load_font(19, bold=True)
    body_font = load_font(14)
    draw.text((28, 20), "HuiTian taskbar icon - multi-size verification", fill="#141A24", font=title_font)
    draw.text((30, 59), "Transparent canvas cropped; taskbar sizes optically enlarged; each frame shown with nearest-neighbour zoom.", fill="#4B5565", font=body_font)

    for index, (frame, stat) in enumerate(zip(frames, frame_stats)):
        row, column = divmod(index, columns)
        x0 = column * cell_width
        y0 = header_height + row * cell_height
        draw.rounded_rectangle((x0 + 12, y0 + 10, x0 + cell_width - 12, y0 + cell_height - 12), radius=16, fill="#FFFFFF", outline="#D9DEE8", width=2)
        draw.text((x0 + 30, y0 + 26), f"{frame.width} x {frame.height}", fill="#111827", font=label_font)
        bbox = stat["alpha_over_0_bbox"]
        draw.text((x0 + 30, y0 + 57), f"Visible bounds: {bbox}", fill="#5B6472", font=body_font)
        draw.text((x0 + 30, y0 + 78), f"Opaque fill: {stat['alpha_over_128_fill_percent']}", fill="#5B6472", font=body_font)

        preview_size = min(176, frame.width * 8)
        preview = checkerboard(preview_size)
        scaled = frame.resize((preview_size, preview_size), Image.Resampling.NEAREST)
        preview.alpha_composite(scaled)
        px = x0 + 30
        py = y0 + 116
        sheet.paste(preview.convert("RGB"), (px, py))
        draw.rectangle((px - 1, py - 1, px + preview_size, py + preview_size), outline="#AEB6C4", width=1)

        taskbar_size = 64
        taskbar = Image.new("RGBA", (taskbar_size, taskbar_size), "#20242C")
        if frame.width <= taskbar_size:
            taskbar_frame = frame
            taskbar_label = "Actual pixels"
        else:
            taskbar_frame = frame.resize((48, 48), Image.Resampling.LANCZOS)
            taskbar_label = "48 px fit preview"
        actual_x = (taskbar_size - taskbar_frame.width) // 2
        actual_y = (taskbar_size - taskbar_frame.height) // 2
        taskbar.alpha_composite(taskbar_frame, (actual_x, actual_y))
        tx = x0 + 256
        ty = y0 + 145
        sheet.paste(taskbar.convert("RGB"), (tx, ty))
        draw.text((tx - 1, ty + 72), taskbar_label, fill="#5B6472", font=body_font)

    output = VALIDATION_DIR / "HuiTianIcon-frames.png"
    sheet.save(output, optimize=True)
    return output


def main() -> None:
    VALIDATION_DIR.mkdir(parents=True, exist_ok=True)
    if not ORIGINAL_PNG.exists():
        ORIGINAL_PNG.write_bytes(ACTIVE_PNG.read_bytes())

    source = Image.open(ORIGINAL_PNG).convert("RGBA")
    original_hash = sha256(ORIGINAL_PNG)
    original_bbox_0 = alpha_bbox(source, 0)
    original_bbox_128 = alpha_bbox(source, 128)
    master, crop_box = make_cropped_master(source)

    # PNG compression changes, but every retained RGBA pixel remains byte-for-byte identical.
    expected_pixels = source.crop(crop_box).tobytes()
    master.save(ACTIVE_PNG, format="PNG", optimize=True)
    reloaded_master = Image.open(ACTIVE_PNG).convert("RGBA")
    if reloaded_master.tobytes() != expected_pixels:
        raise RuntimeError("Saved PNG pixels differ from the source crop.")

    frames = save_multisize_ico(master)
    entries = ico_directory(ACTIVE_ICO)
    actual_sizes = [(entry["width"], entry["height"]) for entry in entries]
    expected_sizes = [(size, size) for size in FRAME_SIZES]
    if actual_sizes != expected_sizes:
        raise RuntimeError(f"ICO frame mismatch: expected {expected_sizes}, got {actual_sizes}")

    frame_dir = VALIDATION_DIR / "frames"
    frame_dir.mkdir(parents=True, exist_ok=True)
    frame_stats: list[dict[str, object]] = []
    for frame in frames:
        size = frame.width
        frame_path = frame_dir / f"HuiTianLogo-{size}.png"
        frame.save(frame_path, format="PNG", optimize=True)
        bbox_0 = alpha_bbox(frame, 0)
        bbox_128 = alpha_bbox(frame, 128)
        if bbox_0 is None or bbox_128 is None:
            raise RuntimeError(f"Generated {size}x{size} frame is unexpectedly transparent.")
        opaque_width = bbox_128[2] - bbox_128[0]
        opaque_height = bbox_128[3] - bbox_128[1]
        frame_stats.append(
            {
                "size": size,
                "alpha_over_0_bbox": bbox_0,
                "alpha_over_128_bbox": bbox_128,
                "alpha_over_128_fill_percent": f"{opaque_width / size:.1%} x {opaque_height / size:.1%}",
                "preview_sha256": sha256(frame_path),
            }
        )

    contact_sheet = make_contact_sheet(frames, frame_stats)
    updated_bbox_0 = alpha_bbox(master, 0)
    updated_bbox_128 = alpha_bbox(master, 128)
    report = {
        "source": {
            "path": str(ORIGINAL_PNG),
            "sha256": original_hash,
            "canvas": source.size,
            "alpha_over_0_bbox": original_bbox_0,
            "alpha_over_128_bbox": original_bbox_128,
        },
        "updated_png": {
            "path": str(ACTIVE_PNG),
            "sha256": sha256(ACTIVE_PNG),
            "canvas": master.size,
            "crop_box_in_source": crop_box,
            "alpha_over_0_bbox": updated_bbox_0,
            "alpha_over_128_bbox": updated_bbox_128,
            "retained_rgba_pixels_exact": True,
        },
        "updated_ico": {
            "path": str(ACTIVE_ICO),
            "sha256": sha256(ACTIVE_ICO),
            "optical_sizing": {
                "maximum_frame_size": OPTICALLY_SIZED_FRAME_MAX,
                "overscan_ratio": OPTICAL_OVERSCAN_RATIO,
            },
            "entries": entries,
            "frames": frame_stats,
        },
        "contact_sheet": str(contact_sheet),
    }
    report_path = VALIDATION_DIR / "icon-audit.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
