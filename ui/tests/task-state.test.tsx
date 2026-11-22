// @vitest-environment jsdom
import React from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

type Message = { type:string; payload:unknown };
const listeners = new Set<(event:{data:Message})=>void>();
const postMessage = vi.fn();
let App: typeof import('../src/main').App;
const game = {appId:42,name:'Test game',path:'D:\\Test',buildId:'1',branch:'public',depots:[]};
const progress = {stage:'校验本地文件',done:7,total:20,current:'Game/dlc.bin'};
const idle = {busy:false,operationKind:null,activeAppId:42,progress:null,connected:true,games:[game],report:null};
const running = {...idle,busy:true,operationKind:'scan',progress};
function emit(type:string,payload:unknown){act(()=>{listeners.forEach(fn=>fn({data:{type,payload}}));});}
function click(name:string){fireEvent.click(screen.getByRole('button',{name,exact:true}));}
function assertRunning(){
 expect((screen.getByRole('button',{name:'正在检测'}) as HTMLButtonElement).disabled).toBe(true);
 expect(screen.getByText('7 / 20')).toBeTruthy();
 expect(screen.getByRole('button',{name:'取消任务'})).toBeTruthy();
}

beforeAll(async()=>{
 Object.defineProperty(window,'chrome',{configurable:true,value:{webview:{postMessage,
  addEventListener:(_type:string,fn:(event:{data:Message})=>void)=>listeners.add(fn),
  removeEventListener:(_type:string,fn:(event:{data:Message})=>void)=>listeners.delete(fn)
 }}});
 App=(await import('../src/main')).App;
});
beforeEach(()=>{postMessage.mockClear();});
afterEach(()=>{cleanup();listeners.clear();});

describe('settings navigation during a scan',()=>{
 it('closing settings returns to the active scan without rediscovering the library',()=>{
  render(<App/>);emit('state',running);click('关于');
  click('关闭窗口');
  expect(postMessage).toHaveBeenLastCalledWith({type:'sync'});
  expect(postMessage.mock.calls.some(([m])=>m.type==='discover')).toBe(false);
  emit('state',running);assertRunning();
 });
 it('allows closing settings while scanning, preserving progress',()=>{
  render(<App/>);emit('state',running);click('关于');
  expect((screen.getByRole('button',{name:'关闭窗口'}) as HTMLButtonElement).disabled).toBe(false);
  click('关闭窗口');
  expect(screen.queryByRole('dialog')).toBeNull();assertRunning();
 });
 it('does not mistake an unrelated command error for scan completion',()=>{
  render(<App/>);emit('state',running);
  emit('error','请等待当前任务结束，或先取消。');assertRunning();
  emit('state',running);assertRunning();
 });
 it('restores the active task after the UI remounts',()=>{
  const first=render(<App/>);emit('state',running);first.unmount();
  postMessage.mockClear();render(<App/>);
  expect(postMessage).toHaveBeenCalledExactlyOnceWith({type:'sync'});
  emit('state',running);assertRunning();
 });
 it('can cancel and start a new scan after returning from settings',()=>{
  render(<App/>);emit('state',running);click('关于');click('关闭窗口');emit('state',running);
  click('取消任务');expect(postMessage).toHaveBeenLastCalledWith({type:'cancel'});
  emit('cancelled','任务已取消');emit('state',idle);
  expect((screen.getByRole('button',{name:'开始检测'}) as HTMLButtonElement).disabled).toBe(false);
  click('开始检测');expect(postMessage).toHaveBeenLastCalledWith({type:'scan',appId:42,mode:'quick'});
  emit('state',running);assertRunning();
 });
 it('retains a completed report when returning from settings',()=>{
  render(<App/>);emit('state',running);click('关于');
  const report={appId:42,scopeComplete:true,depots:[],warnings:[],files:[{path:'dlc.bin',status:'match',localSize:1,officialSize:1,source:'DLC'}]};
  emit('report',report);emit('state',{...idle,report});
  click('关闭窗口');emit('state',{...idle,report});
  expect(screen.getByText('dlc.bin')).toBeTruthy();
  expect((screen.getByRole('button',{name:'导出报告'}) as HTMLButtonElement).disabled).toBe(false);
 });
 it('recovers start controls after a scan is rejected before it starts',()=>{
  render(<App/>);emit('state',idle);click('开始检测');
  emit('error','Steam 正在更新或安装未完成');emit('state',idle);
  expect((screen.getByRole('button',{name:'开始检测'}) as HTMLButtonElement).disabled).toBe(false);
  expect(screen.getByText('Steam 正在更新或安装未完成')).toBeTruthy();
 });
});

describe('deleting extra files',()=>{
 const files=[
  {path:'Game/official.bin',status:'match',localSize:4,officialSize:4,source:'base'},
  {path:'Saved/a.log',status:'extra',localSize:1024,officialSize:null,source:'未匹配'},
  {path:'Saved/b.log',status:'extra',localSize:2048,officialSize:null,source:'未匹配'}
 ];
 const complete={appId:42,scopeComplete:true,depots:[],warnings:[],files};
 it('selects all extra files with one click and sends only their paths',()=>{
  render(<App/>);emit('state',{...idle,report:complete});
  expect(screen.queryByRole('checkbox',{name:'选择 Game/official.bin'})).toBeNull();
  click('全选额外文件');
  fireEvent.click(screen.getByRole('button',{name:/Saved\//}));
  expect((screen.getByRole('checkbox',{name:'选择 Saved/a.log'}) as HTMLInputElement).checked).toBe(true);
  expect((screen.getByRole('checkbox',{name:'选择 Saved/b.log'}) as HTMLInputElement).checked).toBe(true);
  fireEvent.click(screen.getByRole('button',{name:/删除所选 2 个/}));
  expect(postMessage).toHaveBeenLastCalledWith({type:'deleteExtra',paths:['Saved/a.log','Saved/b.log']});
  click('取消全选');
  expect((screen.getByRole('button',{name:/删除所选/}) as HTMLButtonElement).disabled).toBe(true);
 });
 it('allows picking a single extra file',()=>{
  render(<App/>);emit('state',{...idle,report:complete});
  fireEvent.click(screen.getByRole('button',{name:/Saved\//}));
  fireEvent.click(screen.getByRole('checkbox',{name:'选择 Saved/b.log'}));
  fireEvent.click(screen.getByRole('button',{name:/删除所选 1 个/}));
  expect(postMessage).toHaveBeenLastCalledWith({type:'deleteExtra',paths:['Saved/b.log']});
 });
 it('clears the selection when the report changes after deletion',()=>{
  render(<App/>);emit('state',{...idle,report:complete});click('全选额外文件');
  const after={...complete,files:[files[0],files[2]]};
  emit('report',after);emit('state',{...idle,report:after});
  expect((screen.getByRole('checkbox',{name:'选择 Saved/b.log'}) as HTMLInputElement).checked).toBe(false);
 });
 it('disables deletion while busy',()=>{
  render(<App/>);emit('state',{...idle,busy:true,operationKind:'delete',report:complete});
  expect((screen.getByRole('button',{name:'全选额外文件'}) as HTMLButtonElement).disabled).toBe(true);
 });
});

describe('custom title bar',()=>{
 it('sends window actions from the controls',()=>{
  render(<App/>);emit('state',idle);
  click('最小化');expect(postMessage).toHaveBeenLastCalledWith({type:'window',action:'minimize'});
  click('最大化');expect(postMessage).toHaveBeenLastCalledWith({type:'window',action:'maximize'});
  click('关闭程序');expect(postMessage).toHaveBeenLastCalledWith({type:'window',action:'close'});
 });
 it('drags from empty title areas but not from buttons, and toggles maximize on double press',()=>{
  const {container}=render(<App/>);emit('state',idle);
  const topbar=container.querySelector('.topbar') as HTMLElement;
  fireEvent.mouseDown(topbar,{button:0,detail:1});expect(postMessage).toHaveBeenLastCalledWith({type:'window',action:'drag'});
  fireEvent.mouseDown(topbar,{button:0,detail:2});expect(postMessage).toHaveBeenLastCalledWith({type:'window',action:'maximize'});
  postMessage.mockClear();
  fireEvent.mouseDown(screen.getByRole('button',{name:/退出登录|登录 Steam/}),{button:0,detail:1});
  expect(postMessage.mock.calls.some(([m])=>m.type==='window')).toBe(false);
 });
 it('shows the restore button when the host reports a maximized window',()=>{
  render(<App/>);emit('state',{...idle,maximized:true});
  expect(screen.getByRole('button',{name:'向下还原'})).toBeTruthy();
 });
});

describe('status copy',()=>{
 it('empty list tells where things stand and what to do next',()=>{
  render(<App/>);emit('state',{...idle,connected:false});
  expect(screen.getByText('尚未检测')).toBeTruthy();
  expect(screen.getByText('点击“开始检测”，免登录读取本机保存的清单进行比对。')).toBeTruthy();
  emit('state',idle);
  expect(screen.getByText('点击“开始检测”，将本地文件与 Steam 在线清单比对。')).toBeTruthy();
 });
 it('shows when the report was produced instead of a disclaimer',()=>{
  render(<App/>);
  const report={appId:42,started:'2026-11-17T05:30:00+00:00',scopeComplete:true,depots:[],warnings:[],files:[{path:'a.bin',status:'match',localSize:1,officialSize:1,source:'base'}]};
  emit('state',{...idle,report});
  expect(screen.getByText(/^检测于 /)).toBeTruthy();
  expect(screen.queryByText(/不下载游戏/)).toBeNull();
 });
 it('about panel lists facts without settings or roadmap text',()=>{
  render(<App/>);emit('state',idle);click('关于');
  const dialog=screen.getByRole('dialog',{name:'关于'});
  expect(dialog.textContent).toContain('开发版本');
  expect(dialog.textContent).toContain('由 Intary 制作');
  expect(dialog.textContent).toContain('版权所有 © 2026 Intary');
  expect(dialog.textContent).not.toMatch(/尚未提供|预览版/);
 });
});

describe('verification mode',()=>{
 it('defaults to quick and sends the chosen mode with the scan',()=>{
  render(<App/>);emit('state',idle);
  expect(screen.getByRole('radio',{name:'快速'}).getAttribute('aria-checked')).toBe('true');
  fireEvent.click(screen.getByRole('radio',{name:'完整'}));click('开始检测');
  expect(postMessage).toHaveBeenLastCalledWith({type:'scan',appId:42,mode:'full'});
 });
 it('labels quick-mode matches as size matches only',()=>{
  render(<App/>);
  const report={appId:42,mode:'quick',scopeComplete:true,depots:[],warnings:[],files:[{path:'a.bin',status:'match',localSize:1,officialSize:1,source:'base'}]};
  emit('state',{...idle,report});
  expect(screen.getAllByText('大小一致').length).toBeGreaterThan(0);
  expect(screen.getByText('文件名与大小一致，未比对内容')).toBeTruthy();
 });
});

describe('login and manifest source',()=>{
 it('scans without login and labels the local-only source',()=>{
  render(<App/>);emit('state',{...idle,connected:false});
  expect(screen.getAllByText('未登录 · 仅读取本地清单').length).toBeGreaterThan(0);
  expect(screen.getByText('免登录 · 仅读取本地清单')).toBeTruthy();
  click('开始检测');expect(postMessage).toHaveBeenLastCalledWith({type:'scan',appId:42,mode:'quick'});
 });
 it('offers QR and password login with optional remember, and no anonymous login',()=>{
  render(<App/>);emit('state',{...idle,connected:false});click('登录 Steam');
  expect(screen.queryByText(/匿名/)).toBeNull();
  fireEvent.click(screen.getByRole('checkbox',{name:/记住登录/}));
  click('开始扫码登录');expect(postMessage).toHaveBeenLastCalledWith({type:'login',method:'qr',remember:true});
 });
 it('sends account and password, then a Steam Guard code when asked',()=>{
  render(<App/>);emit('state',{...idle,connected:false});click('登录 Steam');
  fireEvent.click(screen.getByRole('tab',{name:'账号密码'}));
  fireEvent.change(screen.getByLabelText('Steam 账号'),{target:{value:'user1'}});
  fireEvent.change(screen.getByLabelText('Steam 密码'),{target:{value:'secret'}});
  click('登录');expect(postMessage).toHaveBeenLastCalledWith({type:'login',method:'password',username:'user1',password:'secret',remember:false});
  emit('guard',{kind:'email',email:'u***@example.com',retry:false});
  expect(screen.getByText('输入发送到 u***@example.com 的验证码')).toBeTruthy();
  fireEvent.change(screen.getByLabelText('Steam 验证码'),{target:{value:'ab12c'}});
  click('提交验证码');expect(postMessage).toHaveBeenLastCalledWith({type:'guardCode',code:'AB12C'});
 });
 it('shows the signed-in account with a logout action',()=>{
  render(<App/>);emit('state',{...idle,connected:true,account:'user1'});
  expect(screen.getByText('已登录 user1')).toBeTruthy();
  click('退出登录');expect(postMessage).toHaveBeenLastCalledWith({type:'logout'});
 });
});

describe('extra files view',()=>{
 const files=[
  {path:'Game/Game.exe',status:'match',localSize:4,officialSize:4,source:'base'},
  {path:'Game/winhttp.dll',status:'extra',localSize:10,officialSize:null,source:'未匹配',kind:'program',facts:['besideExecutable']},
  {path:'BepInEx/plugins/Old.dll',status:'extra',localSize:20,officialSize:null,source:'未匹配',kind:'program',facts:['olderThanUpdate']},
  {path:'BepInEx/config/a.cfg',status:'extra',localSize:30,officialSize:null,source:'未匹配',kind:'config',facts:[]}
 ];
 const report={appId:42,started:'2026-11-22T10:00:00Z',scopeComplete:true,depots:[],warnings:[],files};
 it('opens on extra files and groups a folder with no official files',()=>{
  render(<App/>);emit('state',{...idle,report});
  expect(document.querySelector('.tabs .active')?.textContent).toMatch(/^额外文件/);
  expect(screen.getByText('BepInEx/')).toBeTruthy();
  expect(screen.getByText(/整个文件夹不在 Steam 清单中 · 2 个文件/)).toBeTruthy();
  expect(screen.queryByText('Old.dll')).toBeNull();
  expect(screen.getByText('winhttp.dll')).toBeTruthy();
 });
 it('folder checkbox selects every file in the folder',()=>{
  render(<App/>);emit('state',{...idle,report});
  fireEvent.click(screen.getByRole('checkbox',{name:'选择文件夹 BepInEx/'}));
  fireEvent.click(screen.getByRole('button',{name:/删除所选 2 个/}));
  expect(postMessage).toHaveBeenLastCalledWith({type:'deleteExtra',paths:['BepInEx/plugins/Old.dll','BepInEx/config/a.cfg']});
 });
 it('filters by observable facts without any risk verdict',()=>{
  render(<App/>);emit('state',{...idle,report});
  fireEvent.click(screen.getByRole('button',{name:/修改时间早于最近一次安装或更新/}));
  expect(screen.queryByText('winhttp.dll')).toBeNull();
  expect(screen.getByText('Old.dll')).toBeTruthy();
  expect(document.body.textContent).not.toMatch(/高风险|建议删除|危险/);
 });
});
