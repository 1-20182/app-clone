# 应用分身 —— 软件图标生成
# 设计：iOS 风格圆角方形（squircle 近似），蓝→靛渐变底，
#       两张错位叠放的圆角"窗口"传达"分身/复制"概念。
# 输出：assets/app.ico (16-256 多尺寸) + assets/icon_1024.png 预览
from PIL import Image, ImageDraw
import os

S = 1024
GRAD_TOP = (10, 132, 255)     # Apple systemBlue
GRAD_BOTTOM = (94, 92, 230)   # Apple systemIndigo

def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m

# 1. 底板：垂直渐变 + 圆角蒙版
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / (S - 1)
    r = int(GRAD_TOP[0] + (GRAD_BOTTOM[0] - GRAD_TOP[0]) * t)
    g = int(GRAD_TOP[1] + (GRAD_BOTTOM[1] - GRAD_TOP[1]) * t)
    b = int(GRAD_TOP[2] + (GRAD_BOTTOM[2] - GRAD_TOP[2]) * t)
    gd.line([(0, y), (S, y)], fill=(r, g, b, 255))
img.paste(grad, (0, 0), rounded_mask(S, 232))  # ~22.6% 圆角

draw = ImageDraw.Draw(img)

# 2. 后窗口：半透明白（被"复制"的那份）
back = Image.new("RGBA", (S, S), (0, 0, 0, 0))
bd = ImageDraw.Draw(back)
bd.rounded_rectangle([318, 210, 806, 698], radius=88, fill=(255, 255, 255, 120))
img = Image.alpha_composite(img, back)

# 3. 前窗口：纯白（"分身"），带一点淡蓝渐变细节条
front = Image.new("RGBA", (S, S), (0, 0, 0, 0))
fd = ImageDraw.Draw(front)
fd.rounded_rectangle([218, 310, 706, 798], radius=88, fill=(255, 255, 255, 255))
# 窗口内的"内容条"：两根浅蓝色圆角条，暗示应用界面
fd.rounded_rectangle([266, 384, 530, 434], radius=25, fill=(10, 132, 255, 70))
fd.rounded_rectangle([266, 470, 658, 520], radius=25, fill=(10, 132, 255, 45))
fd.rounded_rectangle([266, 556, 590, 606], radius=25, fill=(10, 132, 255, 28))
# 窗口标题点（红黄绿三点的抽象：一个小圆角块）
fd.rounded_rectangle([266, 342, 658, 358], radius=8, fill=(10, 132, 255, 90))
img = Image.alpha_composite(img, front)

# 4. 柔和投影：前窗口底下淡淡加深（手动画一圈半透明底）
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
sd = ImageDraw.Draw(shadow)
sd.rounded_rectangle([228, 330, 726, 818], radius=96, fill=(20, 30, 90, 45))
shadow = shadow.filter(__import__("PIL.ImageFilter", fromlist=["GaussianBlur"]).GaussianBlur(18))
# 投影要压在底板渐变上、窗口下——重建合成顺序
base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
base.paste(grad, (0, 0), rounded_mask(S, 232))
base = Image.alpha_composite(base, shadow)
back2 = Image.new("RGBA", (S, S), (0, 0, 0, 0))
bd2 = ImageDraw.Draw(back2)
bd2.rounded_rectangle([318, 210, 806, 698], radius=88, fill=(255, 255, 255, 120))
base = Image.alpha_composite(base, back2)
front2 = Image.new("RGBA", (S, S), (0, 0, 0, 0))
fd2 = ImageDraw.Draw(front2)
fd2.rounded_rectangle([218, 310, 706, 798], radius=88, fill=(255, 255, 255, 255))
fd2.rounded_rectangle([266, 384, 530, 434], radius=25, fill=(10, 132, 255, 70))
fd2.rounded_rectangle([266, 470, 658, 520], radius=25, fill=(10, 132, 255, 45))
fd2.rounded_rectangle([266, 556, 590, 606], radius=25, fill=(10, 132, 255, 28))
fd2.rounded_rectangle([266, 342, 658, 358], radius=8, fill=(10, 132, 255, 90))
base = Image.alpha_composite(base, front2)

os.makedirs(r"D:\fenshen\assets", exist_ok=True)
base.save(r"D:\fenshen\assets\icon_1024.png")

# 5. 多尺寸 ICO（LANCZOS 缩放）
ico_sizes = [256, 128, 64, 48, 32, 24, 16]
base.save(r"D:\fenshen\assets\app.ico",
          format="ICO",
          sizes=[(s, s) for s in ico_sizes])

# 6. 预览小图
base.resize((384, 384), Image.LANCZOS).save(r"D:\fenshen\assets\icon_preview.png")
print("icon generated")
