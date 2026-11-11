// 社区补丁设置子页（统一设置的画面组进入；数据层 cpatch_model.m）：读包根 cpatch.tsv，按 宽屏 / 60 帧 / 画面 分组列开关。
//   宽屏组、60 帧组的第一行是这两类的总开关，直接用设置里已有的 S2AWidescreen / S2AFps60（settings.m、Settings.bundle 同一个键，不另起）；
//   帧率类社区项（编号 fps:）跟 60 帧开关走（runtime/src/cpatch.c s2a_cpatch_fps_follow），这里只显示、不单独开关。主标题中文名（cpatch_model.m 对照表），
//   副标题英文原名 + 「社区 · 我方已验 / 未经验证」。下次启动生效
#import "cpatch_settings.h"
#import "settings.h"   // s2a_ios_settings_defaults：社区补丁按游戏存（11-11）
#import "s2a_l10n.h"

static BOOL default_on(NSString *key) { id v = [s2a_ios_settings_defaults() objectForKey:key]; return v ? [v boolValue] : YES; }   // 两个总开关缺省开

@interface S2ACPatchSettings : UITableViewController
@end
@implementation S2ACPatchSettings {
    NSArray<S2ACPatchGroup *> *_groups;
}
- (instancetype)initWithGroups:(NSArray<S2ACPatchGroup *> *)groups {
    if (!(self = [super initWithStyle:UITableViewStyleInsetGrouped])) return nil;
    _groups = groups; self.title = S2AL(@"cpatch.title");
    return self;
}
- (void)viewDidLoad {
    [super viewDidLoad];
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(updateMaterial) name:UIAccessibilityReduceTransparencyStatusDidChangeNotification object:nil];
    [self updateMaterial];
    UILabel *notice = [UILabel new];
    notice.text = S2AL(@"cpatch.notice"); notice.numberOfLines = 0; notice.font = [UIFont preferredFontForTextStyle:UIFontTextStyleFootnote]; notice.adjustsFontForContentSizeCategory = YES;
    notice.textColor = UIColor.secondaryLabelColor; notice.accessibilityIdentifier = @"cpatch.notice";
    UIView *header = [UIView new]; [header addSubview:notice];
    notice.translatesAutoresizingMaskIntoConstraints = NO;
    [NSLayoutConstraint activateConstraints:@[
        [notice.leadingAnchor constraintEqualToAnchor:header.layoutMarginsGuide.leadingAnchor],
        [notice.trailingAnchor constraintEqualToAnchor:header.layoutMarginsGuide.trailingAnchor],
        [notice.topAnchor constraintEqualToAnchor:header.topAnchor constant:12],
        [notice.bottomAnchor constraintEqualToAnchor:header.bottomAnchor constant:-4]]];
    self.tableView.tableHeaderView = header;
}
- (void)viewDidLayoutSubviews {   // 表头按文字高度定高
    [super viewDidLayoutSubviews];
    UIView *h = self.tableView.tableHeaderView;
    CGFloat height = [h systemLayoutSizeFittingSize:CGSizeMake(self.tableView.bounds.size.width, 0) withHorizontalFittingPriority:UILayoutPriorityRequired verticalFittingPriority:UILayoutPriorityFittingSizeLevel].height;
    if (fabs(h.frame.size.height - height) > 0.5) { h.frame = CGRectMake(0, 0, self.tableView.bounds.size.width, height); self.tableView.tableHeaderView = h; }
}
- (void)dealloc { [NSNotificationCenter.defaultCenter removeObserver:self]; }
- (void)updateMaterial {
    BOOL solid = UIAccessibilityIsReduceTransparencyEnabled();
    self.tableView.backgroundColor = solid ? UIColor.systemGroupedBackgroundColor : UIColor.clearColor;
    self.tableView.backgroundView = solid ? nil : [[UIVisualEffectView alloc] initWithEffect:[UIBlurEffect effectWithStyle:UIBlurEffectStyleSystemMaterial]];
}

static NSString *master_key(S2ACPatchGroup *g) { return [g.kind isEqual:@"widescreen"] ? @"S2AWidescreen" : [g.kind isEqual:@"fps"] ? @"S2AFps60" : nil; }
- (NSInteger)numberOfSectionsInTableView:(UITableView *)t { (void)t; return (NSInteger)_groups.count; }
- (NSInteger)tableView:(UITableView *)t numberOfRowsInSection:(NSInteger)s { (void)t; return (NSInteger)_groups[s].items.count + (master_key(_groups[s]) ? 1 : 0); }
- (NSString *)tableView:(UITableView *)t titleForHeaderInSection:(NSInteger)s { (void)t; return _groups[s].title; }
- (NSString *)tableView:(UITableView *)t titleForFooterInSection:(NSInteger)s {
    (void)t;
    if ([_groups[s].kind isEqual:@"fps"]) return S2AL(@"cpatch.footer.fps");
    return S2AL(@"cpatch.footer");
}
- (UITableViewCell *)tableView:(UITableView *)t cellForRowAtIndexPath:(NSIndexPath *)ip {
    (void)t;
    S2ACPatchGroup *g = _groups[ip.section]; NSString *mk = master_key(g);
    UITableViewCell *cell = [[UITableViewCell alloc] initWithStyle:UITableViewCellStyleSubtitle reuseIdentifier:nil];
    cell.selectionStyle = UITableViewCellSelectionStyleNone;
    cell.textLabel.font = [UIFont preferredFontForTextStyle:UIFontTextStyleBody]; cell.textLabel.adjustsFontForContentSizeCategory = YES;
    cell.detailTextLabel.font = [UIFont preferredFontForTextStyle:UIFontTextStyleFootnote]; cell.detailTextLabel.adjustsFontForContentSizeCategory = YES;
    UISwitch *sw = [UISwitch new]; cell.accessoryView = sw;
    cell.textLabel.numberOfLines = 0; cell.detailTextLabel.numberOfLines = 0; cell.detailTextLabel.textColor = UIColor.secondaryLabelColor;
    if (mk && ip.row == 0) {
        cell.textLabel.text = [mk isEqual:@"S2AWidescreen"] ? S2AL(@"cpatch.group.widescreen") : S2AL(@"cpatch.group.fps");
        cell.detailTextLabel.text = [mk isEqual:@"S2AWidescreen"] ? S2AL(@"cpatch.master.widescreen.note") : S2AL(@"cpatch.master.fps.note");
        sw.on = default_on(mk); sw.accessibilityLabel = cell.textLabel.text; sw.accessibilityIdentifier = [@"cpatch.master." stringByAppendingString:g.kind];
        [sw addTarget:self action:@selector(masterChanged:) forControlEvents:UIControlEventValueChanged];
        sw.tag = ip.section;
        return cell;
    }
    S2ACPatchItem *it = g.items[ip.row - (mk ? 1 : 0)];
    cell.textLabel.text = it.title;
    NSString *lic = it.license.length && ![it.license hasPrefix:@"none"] ? it.license : S2AL(@"cpatch.license.none");   // 出包表里「没声明」写作空或 none(未声明)
    // 缺省开 = 出包时在 graphics/cpatch/verified.tsv 里（我方验过本款这一份，pack/stage_rewrite.sh）；其余是社区未经验证、缺省关
    NSString *tag = it.defaultOn ? S2AL(@"cpatch.tag.verified") : S2AL(@"cpatch.tag.unverified");
    cell.detailTextLabel.text = [NSString stringWithFormat:S2AL(@"cpatch.item.detail"), it.name, tag, lic, it.followsFps60 ? S2AL(@"cpatch.item.followsFps") : @""];
    sw.accessibilityLabel = it.title; sw.accessibilityIdentifier = [@"cpatch.item." stringByAppendingString:it.ident];
    if (it.followsFps60) { sw.on = default_on(@"S2AFps60"); sw.enabled = NO; }
    else {
        sw.on = [S2ACPatchEnabled(_groups, s2a_ios_settings_defaults()) containsObject:it.ident];
        [sw addTarget:self action:@selector(itemChanged:) forControlEvents:UIControlEventValueChanged];
    }
    return cell;
}
- (void)masterChanged:(UISwitch *)sw {
    NSString *mk = master_key(_groups[sw.tag]); if (!mk) return;
    [s2a_ios_settings_defaults() setBool:sw.on forKey:mk];
    [self.tableView reloadSections:[NSIndexSet indexSetWithIndex:(NSUInteger)sw.tag] withRowAnimation:UITableViewRowAnimationNone];   // 帧率类社区项跟着变
}
- (void)itemChanged:(UISwitch *)sw {
    NSString *ident = [sw.accessibilityIdentifier substringFromIndex:@"cpatch.item.".length];
    S2ACPatchSetEnabled(_groups, s2a_ios_settings_defaults(), ident, sw.on);
}
@end

// 包根没有 cpatch.tsv 或表里没有项时，统一设置不显示此入口。
NSArray<S2ACPatchGroup *> *s2a_cpatch_bundled_groups(void) {
    NSString *path = [NSBundle.mainBundle pathForResource:@"cpatch" ofType:@"tsv"];
    NSString *text = path ? [NSString stringWithContentsOfFile:path encoding:NSUTF8StringEncoding error:nil] : nil;
    return text ? S2ACPatchGroupsFromTSV(text) : @[];
}
UIViewController *s2a_cpatch_settings_controller(NSArray<S2ACPatchGroup *> *groups) {
    return groups.count ? [[S2ACPatchSettings alloc] initWithGroups:groups] : nil;
}
