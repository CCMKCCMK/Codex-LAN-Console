'use strict';
(() => {
  const q = s => document.querySelector(s), dialog = q('#scooterDialog');
  const endpoint = '/commute/scooter';
  let view, inFlight = false, watcher = null, sequence = Date.now(), points = [], syncing = false, activeId = '', map, layer, historyKey = '', settingsRevision, pendingAction;
  const native = () => window.CodexAndroidScooter;
  const uuid = () => { const b = new Uint8Array(16); crypto.getRandomValues(b); b[6]=(b[6]&15)|64;b[8]=(b[8]&63)|128;return [...b].map((v,i)=>([4,6,8,10].includes(i)?'-':'')+v.toString(16).padStart(2,'0')).join(''); };
  const text = (id,value) => { q(id).textContent=value; };
  const km = n => `${Number(n||0).toFixed(2)} km`;
  const error = e => text('#scooterError',e?.message||String(e||''));
  const active = () => view?.data.rides.find(r=>!r.stoppedAt);
  try { pendingAction=JSON.parse(localStorage.getItem('scooterAction')||'null'); } catch {}
  const saveQueue = () => { try { localStorage.setItem('scooterPending',JSON.stringify({rideId:activeId,points})); } catch { error('离线空间不足，请保持联网并停止记录，避免继续丢失定位。'); } };
  try { const saved=JSON.parse(localStorage.getItem('scooterPending')||'null');if(saved){activeId=saved.rideId;points=saved.points||[];sequence=Math.max(sequence,...points.map(p=>p.seq+1));} } catch {}
  function tracking() {
    if(native()){try{const s=JSON.parse(native().status());return s.message||'Android 后台记录可用';}catch{return 'Android 记录状态暂不可用';}}
    return watcher!==null?`网页定位中 · 待同步 ${points.length} 点；请保持页面在前台`:'自动后台记录需要新版 Android；HTTPS 网页仅支持前台定位。';
  }
  function render() {
    if(!view)return;
    const {data,model,estimate:e}=view, r=active();
    text('#scooterPercent',data.cycles.length?`${Math.round(e.percent)}%`:'—');text('#scooterRange',data.cycles.length?km(e.remainingKm):'—');
    text('#scooterConfidence',`${model.confidence} · ${model.cycles} 个有效完整周期`);q('#scooterProgress').value=e.percent;
    text('#scooterAdvice',e.message);q('.scooter-battery').classList.toggle('risk',e.returnAtRisk===true);
    text('#scooterDistance',km(e.usedKm));text('#scooterMinutes',`${Math.round(e.usedMinutes)} 分钟`);text('#scooterClimb',`${Math.round(e.ascent)} / ${Math.round(e.descent)} m`);
    text('#scooterTracking',tracking());
    q('#scooterResume').hidden=!r;
    q('#scooterRetry').hidden=!pendingAction;
    for(const b of dialog.querySelectorAll('[data-scooter]'))b.disabled=inFlight||(['full','start'].includes(b.dataset.scooter)?!!r:b.dataset.scooter==='stop'?!r:!data.cycles.some(c=>!c.endedAt));
    const route=e.returnRoute;
    text('#scooterReturn',route?`${data.settings.charger.name} · 返回 ${km(route.meters/1000)} · 上坡 ${Math.round(route.ascent)} m${route.terrainAvailable?'':' · 地形不完整，额外预留余量'}`:e.positionFresh?'正在获取真实的返回路线…':'等待本次骑行的最新定位。');
    if(dialog.open&&route?.line?.length&&window.L){
      if(!map){map=L.map('scooterMap',{zoomControl:false}).setView(route.line[0],14);L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',{attribution:'© OpenStreetMap © CARTO'}).addTo(map);}
      map.invalidateSize();
      const key=JSON.stringify(route.line);if(layer?.routeKey!==key){if(layer)map.removeLayer(layer);layer=L.polyline(route.line,{color:themeColor('--green'),weight:5}).addTo(map);layer.routeKey=key;map.fitBounds(layer.getBounds(),{padding:[20,20],maxZoom:17});}
    }
    const rows=data.cycles.map(c=>{const rides=data.rides.filter(r=>r.cycleId===c.id);return {c,km:rides.reduce((n,r)=>n+r.meters,0)/1000,minutes:rides.reduce((n,r)=>n+r.minutes,0)};});
    const key=JSON.stringify(rows.map(r=>[r.c.id,r.c.endReason,r.km]));
    if(key!==historyKey){historyKey=key;
      const last=rows.slice(-12),max=Math.max(1,...last.map(r=>r.km)),step=last.length?600/last.length:600;
      q('#scooterChart').innerHTML=last.length?`<svg viewBox="0 0 660 180" role="img" aria-label="每个充电周期的实走公里数"><path d="M40 15V145H645" fill="none" stroke="currentColor" opacity=".3"/>${last.map((r,i)=>`<g><rect x="${48+i*step}" y="${140-r.km/max*105}" width="${Math.max(8,step-18)}" height="${Math.max(2,r.km/max*105)}" rx="4" fill="${r.c.endReason==='depleted'?themeColor('--green'):'#708d86'}"><title>${esc(new Date(r.c.fullAt).toLocaleDateString())} · ${km(r.km)} · ${r.c.endReason==='depleted'?'已用尽':r.c.endReason?'提前充电':'进行中'}</title></rect><text x="${50+i*step}" y="165" fill="currentColor" font-size="12">${i+1}</text><text x="${50+i*step}" y="${130-r.km/max*105}" fill="currentColor" font-size="12">${r.km.toFixed(1)}</text></g>`).join('')}<text x="4" y="20" fill="currentColor" font-size="12">km</text></svg>`:'<p class="field-help">你的第一条完整记录会出现在这里。</p>';
      q('#scooterHistory').innerHTML=rows.slice(-8).reverse().map(r=>`<div class="scooter-history-row"><span>${esc(new Date(r.c.fullAt).toLocaleString('zh-CN',{month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'}))}<br><small>${r.c.endReason==='depleted'?'已记录用尽':r.c.endReason?'提前充电 · 不用于完整容量标定':'当前充电周期'}</small></span><strong>${km(r.km)}</strong></div>`).join('');
    }
    text('#scooterLearning',`坡度先按物理近似修正，容量按有效完整周期校准。至少 3 个完整周期后给出初步结果；无法保证两三周就足够准确。${model.backtestErrorPercent===null?'样本不足，暂无历史预测误差。':`历史逐周期预测平均误差 ${model.backtestErrorPercent.toFixed(1)}%，不代表下一次误差上限。`}`);
    if(view.loadWarning)error(view.loadWarning);
  }
  async function load(){try{view=await api(endpoint);render();}catch(e){error(e);}}
  async function sync(){
    if(syncing||!points.length||!activeId)return;
    syncing=true;const sent=points.slice(0,100),rideId=activeId;
    try{view=await api(endpoint+'/points','POST',{rideId,points:sent});points=points.filter(p=>p.seq>sent.at(-1).seq);saveQueue();render();}catch(e){error('定位暂存手机，联网后会重试：'+e.message);}finally{syncing=false;}
  }
  function stopWatch(){if(watcher!==null)navigator.geolocation.clearWatch(watcher);watcher=null;}
  async function begin(r){
    if(activeId&&activeId!==r.id&&points.length){await sync();if(points.length)throw new Error('上一段定位尚未同步，请先联网同步。');}
    activeId=r.id;sequence=Math.max(Date.now(),r.lastSeq+1);
    if(native()){const result=native().start(r.id);if(!['started','permission_requested'].includes(result))throw new Error(result);return;}
    if(!window.isSecureContext||!navigator.geolocation){error('当前 HTTP 页面不能定位。可先记录时长、停止时补里程；后台自动记录需要安装新版 Android。');return;}
    stopWatch();watcher=navigator.geolocation.watchPosition(p=>{
      if(points.length>=20000){stopWatch();error('离线定位队列已满，已停止采集。请联网同步。');return;}
      points.push({seq:sequence++,at:new Date(p.timestamp).toISOString(),lat:p.coords.latitude,lon:p.coords.longitude,accuracy:p.coords.accuracy});saveQueue();sync();
    },e=>error('定位失败：'+e.message),{enableHighAccuracy:true,maximumAge:3000,timeout:15000});
  }
  async function action(name){
    if(inFlight)return;
    if(name==='full'&&!confirm('确认 Scooter 已充满？当前未用尽的周期会保留，但不用于完整容量标定。'))return;
    if(name==='empty'&&!confirm('确认已经没电或因低电量无法继续骑行？不要为了测试勉强骑到断电。'))return;
    inFlight=true;error('');render();
    try{
      const r=active();
      if(['stop','empty'].includes(name)){
        stopWatch();
        if(native())native().stop();
        await sync();
      }
      const manual=q('#scooterManualKm').value;
      const payload={action:name,rideId:r?.id||null,distanceKm:['stop','empty'].includes(name)&&manual!==''?Number(manual):null};
      if(pendingAction&&pendingAction.action!==name)throw new Error('上一条操作尚未确认，请先重试同一按钮，避免重复记录。');
      if(!pendingAction){pendingAction={...payload,requestId:uuid()};localStorage.setItem('scooterAction',JSON.stringify(pendingAction));}
      view=await api(endpoint+'/action','POST',pendingAction);
      pendingAction=null;localStorage.removeItem('scooterAction');
      q('#scooterManualKm').value='';
      if(name==='start')await begin(active());
      render();
    }catch(e){if(e.status>=400&&e.status<500&&e.status!==408&&e.status!==429){pendingAction=null;localStorage.removeItem('scooterAction');}error(e);}finally{inFlight=false;render();}
  }
  function fillSettings(){if(!view)return;const s=view.data.settings;settingsRevision=view.data.revision;
    for(const [id,v] of Object.entries({Charger:s.charger.name,Lat:s.charger.lat,Lon:s.charger.lon,Reference:s.referenceRangeKm,Mass:s.totalMassKg,Reserve:s.reservePercent,Interval:s.alertSeconds}))q('#scooter'+id).value=v;
    q('#scooterAlerts').checked=s.alertsEnabled;q('#scooterTerrain').checked=s.terrainEnabled;
  }
  document.addEventListener('click',async e=>{if(e.target.closest('#openScooter')){await load();if(view)fillSettings();setTimeout(()=>map?.invalidateSize(),100);}const b=e.target.closest('[data-scooter]');if(b)action(b.dataset.scooter);});
  q('#scooterSettings').addEventListener('toggle',()=>{if(q('#scooterSettings').open)fillSettings();});
  q('#scooterResume').onclick=async()=>{try{await load();if(active())await begin(active());render();}catch(e){error(e);}};
  q('#scooterRetry').onclick=()=>{if(pendingAction)action(pendingAction.action);};
  q('#scooterHome').onclick=()=>{if(state){q('#scooterCharger').value=state.settings.home.name;q('#scooterLat').value=state.settings.home.lat;q('#scooterLon').value=state.settings.home.lon;}};
  q('#scooterFind').onclick=async()=>{try{const p=await api('/commute/location?query='+encodeURIComponent(q('#scooterCharger').value));q('#scooterCharger').value=p.name;q('#scooterLat').value=p.lat;q('#scooterLon').value=p.lon;}catch(e){error(e);}};
  q('#scooterSettingsForm').onsubmit=async e=>{e.preventDefault();try{view=await api(endpoint+'/settings','PUT',{revision:settingsRevision,settings:{charger:{name:q('#scooterCharger').value,lat:+q('#scooterLat').value,lon:+q('#scooterLon').value},referenceRangeKm:+q('#scooterReference').value,totalMassKg:+q('#scooterMass').value,reservePercent:+q('#scooterReserve').value,alertSeconds:+q('#scooterInterval').value,alertsEnabled:q('#scooterAlerts').checked,terrainEnabled:q('#scooterTerrain').checked}});settingsRevision=view.data.revision;render();toast('Scooter 设置已保存');}catch(e){error(e);}};
  q('#scooterExport').onclick=async()=>{try{const data=await api(endpoint+'/export');const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([JSON.stringify(data,null,2)],{type:'application/json'}));a.download='scooter-statistics.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),60000);}catch(e){error(e);}};
  setInterval(()=>{if(dialog.open&&!document.hidden){if(!inFlight)load();text('#scooterTracking',tracking());}sync();},15000);
  setInterval(()=>{const r=active();if(r&&dialog.open)text('#scooterMinutes',`${Math.floor((view.estimate.usedMinutes-r.minutes)+(Date.now()-new Date(r.startedAt))/60000)} 分钟 · 本段 ${Math.max(0,Math.floor((Date.now()-new Date(r.startedAt))/1000))} 秒`);},1000);
  if(new URLSearchParams(location.search).get('panel')==='scooter'){dialog.showModal();load().then(fillSettings);}
})();
