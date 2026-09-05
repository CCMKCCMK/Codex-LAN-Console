"""Create original captioned promo videos from one actual, privacy-clean UI capture.
No fabricated ride data and no third-party music. Audio is locally generated TTS.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import subprocess, wave
import imageio_ffmpeg

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'release'/'promo'
FFMPEG=imageio_ffmpeg.get_ffmpeg_exe()
FONT='C:/Windows/Fonts/msyh.ttc'
BOLD='C:/Windows/Fonts/msyhbd.ttc'
BG='#0a1216'; CARD='#132329'; MINT='#7af1c7'; TEXT='#edf6f3'; MUTED='#9ab2ad'
SLIDES=[
 ('手机遥控电脑', '顺便，把通勤管起来', ['任务 · 远程操作 · 通勤', '一个自用工具，公开给你'], 'intro'),
 ('Scooter 续航实验', '不是猜一格电，而是留下一段记录', ['已充满 → 开始骑行 → 停止骑行', '里程 / 时间 / 上坡与下坡'], 'screen'),
 ('回充电点，还够吗？', '把返回路线与地形一起考虑', ['真实骑行路线 + 保守余量', '缺少数据时，明确显示无法判断'], 'route'),
 ('断网先记录', '联网后同步，不重复计算', ['Android 主动开启定位服务', '允许位置与通知，才开始记录'], 'offline'),
 ('把真实数据慢慢积累', '现在，MIT 开源', ['GitHub  /  CCMKCCMK', 'Codex-LAN-Console'], 'end'),
]
def font(n,bold=False):return ImageFont.truetype(BOLD if bold else FONT,n)
def draw_card(w,h,item):
    title,subtitle,lines,kind=item
    im=Image.new('RGB',(w,h),BG);d=ImageDraw.Draw(im)
    portrait=h>w;pad=int(w*.075)
    def t(x,y,s,n=32,color=TEXT,b=False):d.text((x,y),s,font=font(n,b),fill=color)
    def fit(s,n,avail):
        while d.textlength(s,font=font(n,True))>avail:n-=1
        return n
    d.ellipse((w-300,-160,w+240,350),fill='#12322e')
    t(pad,45,'CODEX LAN CONSOLE   /   1.9.0',19,MINT,True)
    y=130 if portrait else 120
    t(pad,y,title,fit(title,56 if portrait else 66,w-2*pad),TEXT,True)
    t(pad,y+90,subtitle,fit(subtitle,27 if portrait else 31,w-2*pad),MUTED)
    top=340 if portrait else 300
    if kind=='screen':
        shot=Image.open(OUT/'scooter-ui.png').convert('RGB')
        width=int(w*.80) if portrait else 560
        height=int(h*.47) if portrait else 330
        # The screenshot is a real empty-state capture, not invented field-test data.
        shot.thumbnail((width,height),Image.Resampling.LANCZOS)
        x=(w-shot.width)//2 if portrait else w-shot.width-pad
        im.paste(shot,(x,top))
        t(x,top+shot.height+12,'实际界面 · 尚无实测周期',17,MUTED)
        if not portrait:
            for i,line in enumerate(lines):t(pad,top+45+i*60,line,26)
    elif kind=='route':
        box=(pad,top,w-pad,top+(430 if portrait else 245))
        d.rounded_rectangle(box,radius=24,fill=CARD,outline='#31534a',width=2)
        x0,y0,x1,y1=box
        pts=[(x0+45,y1-65),(x0+(x1-x0)*.32,y1-125),(x0+(x1-x0)*.58,y0+115),(x1-50,y0+60)]
        d.line(pts,fill=MINT,width=9,joint='curve')
        for x,y2 in (pts[0],pts[-1]):d.ellipse((x-11,y2-11,x+11,y2+11),fill=TEXT)
        t(x0+25,y1-45,'当前位置',19);t(x1-150,y0+15,'充电点',19)
        t(x0+25,y0+20,'示意图 · 非实际路线',16,MUTED)
        for i,line in enumerate(lines):t(pad,box[3]+30+i*48,line,fit(line,27,w-2*pad))
    else:
        for i,line in enumerate(lines):
            yy=top+i*(120 if portrait else 105)
            d.rounded_rectangle((pad,yy,w-pad,yy+85),radius=20,fill=CARD,outline='#25483e')
            t(pad+24,yy+23,line,fit(line,29 if portrait else 34,w-2*pad-48),TEXT,kind=='end')
        if kind=='offline':
            t(pad,top+(300 if portrait else 235),'手机暂存  →  联网重传  →  序号去重',fit('手机暂存  →  联网重传  →  序号去重',30,w-2*pad),MINT,True)
        if kind=='end':
            t(pad,top+(330 if portrait else 240),'欢迎使用、反馈与贡献',32,MINT,True)
    t(pad,h-105,'实验性估算，不是车载电池读数。',21,MUTED)
    t(pad,h-70,'独立个人项目 · 非 OpenAI / UCSD 官方产品',17,MUTED)
    return im

def make(name,w,h,audio):
    with wave.open(str(audio),'rb') as a:duration=a.getnframes()/a.getframerate()+1.2
    lengths=[duration*x for x in [.15,.23,.25,.18,.19]]
    chunks=[]
    for i,(slide,seconds) in enumerate(zip(SLIDES,lengths)):
        png=OUT/f'{name}-{i}.png';draw_card(w,h,slide).save(png)
        if i==0:draw_card(w,h,slide).save(OUT/f'{name}-cover.jpg',quality=94)
        chunk=OUT/f'{name}-{i}.mp4';chunks.append(chunk)
        subprocess.run([FFMPEG,'-y','-loglevel','error','-loop','1','-i',str(png),
          '-t',str(seconds),'-vf',f'fade=t=in:st=0:d=0.22,fade=t=out:st={max(.3,seconds-.22)}:d=0.22',
          '-r','24','-c:v','libx264','-preset','veryfast','-crf','21','-pix_fmt','yuv420p','-threads','2',str(chunk)],check=True)
    playlist=OUT/f'{name}-concat.txt'
    playlist.write_text(''.join(f"file '{p.as_posix()}'\n" for p in chunks),encoding='utf-8')
    final=OUT/f'{name}.mp4'
    subprocess.run([FFMPEG,'-y','-loglevel','error','-f','concat','-safe','0','-i',str(playlist),
       '-i',str(audio),'-map','0:v','-map','1:a','-c:v','copy','-c:a','aac','-b:a','128k',
       '-af','apad=pad_dur=1.2','-t',str(duration),'-movflags','+faststart',str(final)],check=True)
    print(f'{final} | {duration:.1f}s | {final.stat().st_size} bytes',flush=True)

if __name__ == '__main__':
    make('bilibili-1.9.0',1280,720,OUT/'voice.wav')
    make('douyin-1.9.0',720,1280,OUT/'voice-short.wav')
    draw_card(900,1200,SLIDES[0]).save(OUT/'douyin-cover-3x4.jpg',quality=94)
    draw_card(1200,900,SLIDES[0]).save(OUT/'douyin-cover-4x3.jpg',quality=94)
