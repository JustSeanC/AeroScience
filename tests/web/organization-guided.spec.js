import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// Synthetic presentation fixtures. The existing actual PostgreSQL/Engine suites
// remain the authority for role, scope, consent and mutation enforcement.
const connection={id:'06000000-0000-0000-0000-000000000001',organizationId:'06000000-0000-0000-0000-000000000002',name:'Synthetic Research Organization — long campaign and instrument requirements',origin:'https://research.example',permission:'manageOwnClaims',expiresUtc:'2099-01-01T00:00:00Z'};
const card={id:'06000000-0000-0000-0000-000000000003',organizationId:connection.organizationId,campaignId:'06000000-0000-0000-0000-000000000004',campaignRevision:3,revision:2,name:'Synthetic frozen airspeed research',objective:'Retain the original flight conditions',instructions:'Keep original units and timestamps.',description:'Issued definition fixture, not simulator acceptance.',policy:{participation:'multiPilot',requiredDistinctPilots:2,maximumActiveClaims:4,allowVolunteers:true,reviewPolicy:'Original recorded evidence and independent server review required.'},definition:{version:1,template:{id:'flight-regime',version:1},flightRegime:{airspeedKnots:120,airspeedToleranceKnots:5,altitudeMslFeet:5000,altitudeToleranceFeet:100,holdSeconds:10}}};
async function fixture(page,{role='Pilot',state='active',offline=false,expired=false,empty=false,permission='manageOwnClaims'}={}){
 const writes=[];let claimCount=0;
 await page.route('**/api/organizations**',async route=>{
  const u=new URL(route.request().url()),path=u.pathname;
  if(route.request().method()!=='GET'){writes.push({path,data:route.request().postDataJSON()});if(path.endsWith('/claim-command')){claimCount++;return route.fulfill({json:{}});}}
  if(path==='/api/organizations')return route.fulfill({json:{vaultAvailable:true,connections:empty?[]:[{...connection,permission}]}});
  if(path==='/api/organizations/live/status')return route.fulfill({json:[]});
  if(path.endsWith('/opportunities'))return route.fulfill({json:{status:offline?'Offline / Unable to Connect':'Connected',page:offline?null:{items:[{card,eligible:true,suspended:false,availableSlots:4,availability:'Available'}],nextOffset:null}}});
  if(path.endsWith('/claims'))return route.fulfill({json:{status:'Connected',page:{items:claimCount?[{id:'06000000-0000-0000-0000-000000000005',issuedCard:card,issuedRevision:2,revision:1,state:'claimed',availability:'Claim confirmed',updateAvailable:true,latestPublishedRevision:7}]:[],nextOffset:null}}});
  if(path==='/api/organizations/'+connection.id)return route.fulfill({json:{connection:{...connection,permission},status:expired?'Expired / Reconnect':offline?'Offline / Unable to Connect':'Connected',information:offline||expired?null:{state,role,campaigns:{items:[],nextOffset:null}}}});
  return route.fulfill({json:{error:'Unsupported synthetic presentation request'},status:400});
 });
 await page.route('**/api/research/organization-cards?*',r=>r.fulfill({json:[]}));
 return writes;
}
const panel=page=>page.locator('#research-organizations');

test('first-time Organizations entry is discoverable, preserves query parameters and keeps personal research offline',async({page})=>{
 const writes=await fixture(page,{empty:true});await page.goto('/#organizations?keep=original&flight=existing-flight');
 await expect(panel(page).getByRole('navigation',{name:'Organization tasks'})).toBeVisible();await expect(page.locator('#organizations-navigation')).toBeVisible();
 await panel(page).getByRole('link',{name:'Available Research',exact:true}).click();await expect(panel(page)).toContainText('Connect to an organization');
 const url=new URLSearchParams((await page.evaluate(()=>location.hash)).split('?')[1]);expect(url.get('keep')).toBe('original');expect(url.get('flight')).toBe('existing-flight');expect(url.get('orgView')).toBe('available');
 await expect(page.locator('#research-starters')).toBeVisible();expect(writes).toEqual([]);
});

test('guided assignment review uses frozen criteria and a single explicit claim without recording, sharing or upload',async({page})=>{
 const writes=await fixture(page);await page.goto('/#organizations?orgView=available&keep=1');const p=panel(page);
 await expect(p.getByRole('region',{name:'Issued research requirements'})).toContainText('120 ± 5 knots IAS');await expect(p).toContainText('2 distinct contribution(s)');
 await p.getByRole('button',{name:'Claim Test Card',exact:true}).click();const dialog=page.getByRole('dialog',{name:'Claim Test Card'});await expect(dialog).toContainText('does not prepare or start a mission');
 await dialog.getByRole('button',{name:'Claim Test Card',exact:true}).evaluate(b=>{b.click();b.click();});await expect(dialog).toHaveCount(0);await expect(p).toContainText('Your revision 2 is frozen');await expect(p.getByRole('button',{name:'Prepare Mission',exact:true})).toBeVisible();
 expect(writes).toHaveLength(1);expect(writes[0].path).toMatch(/claim-command$/);expect(writes[0].data.publishedRevision).toBe(2);expect(writes[0].data.action).toBe('claim');
});

test('read-only, expired and unauthorized member presentation never exposes administration or claim mutations',async({page})=>{
 for(const scenario of [{role:'Pilot',permission:'readPublished'},{role:'Reviewer',permission:'readPublished'},{role:'Administrator',offline:true},{role:'Administrator',expired:true},{role:'Manager',state:'suspended'}]){
  const writes=await fixture(page,scenario);await page.goto('/#organizations?orgView=available');const p=panel(page);await expect(p).toContainText(scenario.expired?'Expired / Reconnect':scenario.offline?'Offline / Unable to Connect':scenario.state==='suspended'?'Membership unavailable':'Active membership');
  await expect(p.getByRole('link',{name:'Organization Administration',exact:true})).toHaveCount(0);if(!scenario.offline&&!scenario.expired&&scenario.state!=='suspended')await expect(p.getByRole('button',{name:'Claim Test Card',exact:true})).toBeDisabled();else await expect(p.getByRole('button',{name:'Claim Test Card',exact:true})).toHaveCount(0);
  expect(writes).toEqual([]);await page.unrouteAll({behavior:'wait'});
 }
});

test('authorized management, task navigation and long issued requirements reflow in both themes at 390px and 200% text',async({page})=>{
 await fixture(page,{role:'Manager'});await page.setViewportSize({width:390,height:844});
 for(const theme of ['light','dark']){
  await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await page.goto('/#organizations?orgView=available');await expect(panel(page).getByRole('region',{name:'Issued research requirements'})).toBeVisible();
  const original=await panel(page).evaluate(e=>parseFloat(getComputedStyle(e).fontSize));await panel(page).evaluate(e=>e.style.fontSize='200%');expect(await panel(page).evaluate(e=>parseFloat(getComputedStyle(e).fontSize))).toBe(original*2);
  await expect(panel(page).getByRole('link',{name:'Manage Campaigns',exact:true})).toHaveAttribute('href',connection.origin);await expect(panel(page).getByRole('link',{name:'Organization Administration',exact:true})).toHaveCount(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);expect((await new AxeBuilder({page}).include('#research-organizations').analyze()).violations).toEqual([]);
  const link=panel(page).getByRole('link',{name:'My Assignments',exact:true});await link.focus();await expect(link).toBeFocused();await page.keyboard.press('Enter');await expect(panel(page).getByRole('heading',{name:'My Assignments',exact:true}).first()).toBeVisible();
 }
});

test('P06 read-only connection cannot silently enter desktop live location scope',async({page})=>{
 const writes=await fixture(page,{permission:'readPublished'}),locations=[];page.on('request',r=>{if(new URL(r.url()).pathname===`/api/organizations/${connection.id}/live`)locations.push(r.url());});
 await page.goto('/#organizations?orgView=live');await expect(panel(page)).toContainText('Read-only organization access does not grant access to locations');await expect(panel(page).getByRole('button',{name:'Authorize Live Sharing',exact:true})).toBeVisible();expect(locations).toEqual([]);expect(writes).toEqual([]);
});
