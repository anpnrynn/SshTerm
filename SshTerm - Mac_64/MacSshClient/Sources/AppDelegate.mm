#import "AppDelegate.h"
#import "PtySession.h"

static NSString * const RecentSessionsKey = @"RecentSessions";
static NSString * const BackspaceBindingKey = @"BackspaceBinding";
static NSString * const BackspaceCustomSequenceKey = @"BackspaceCustomSequence";
static NSString * const DeleteBindingKey = @"DeleteBinding";
static NSString * const DeleteCustomSequenceKey = @"DeleteCustomSequence";
static NSString * const ClearLogsOnExitKey = @"ClearLogsOnExit";
static NSString * const ActiveSessionsChangedNotification = @"ActiveSessionsChanged";
static NSInteger const MaxRecentSessions = 1000;

@class TerminalView;
@class TerminalTab;

static NSString *SequenceFromEscapedString(NSString *escaped) {
    NSString *trimmed = [escaped stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    if (!trimmed.length) return @"";

    NSString *upper = trimmed.uppercaseString;
    if ([upper isEqualToString:@"DEL"]) return @"\x7f";
    if ([upper isEqualToString:@"BS"] || [upper isEqualToString:@"BACKSPACE"] || [upper isEqualToString:@"CTRL-H"]) return @"\b";
    if ([upper isEqualToString:@"ESC"]) return @"\x1b";
    if ([upper hasPrefix:@"ESC"]) {
        return [@"\x1b" stringByAppendingString:[trimmed substringFromIndex:3]];
    }
    if ([trimmed hasPrefix:@"^"] && trimmed.length == 2) {
        unichar control = [trimmed.uppercaseString characterAtIndex:1];
        if (control >= '@' && control <= '_') return [NSString stringWithFormat:@"%C", (unichar)(control - '@')];
    }

    unsigned value = 0;
    NSScanner *hexScanner = [NSScanner scannerWithString:trimmed];
    if ([trimmed hasPrefix:@"0x"] || [trimmed hasPrefix:@"0X"]) {
        if ([hexScanner scanHexInt:&value] && hexScanner.isAtEnd && value <= 0xff) {
            return [NSString stringWithFormat:@"%C", (unichar)value];
        }
    }

    NSScanner *decimalScanner = [NSScanner scannerWithString:trimmed];
    NSInteger decimalValue = 0;
    if ([decimalScanner scanInteger:&decimalValue] && decimalScanner.isAtEnd && decimalValue >= 0 && decimalValue <= 255) {
        return [NSString stringWithFormat:@"%C", (unichar)decimalValue];
    }

    NSMutableString *sequence = [NSMutableString string];
    for (NSUInteger i = 0; i < escaped.length; i++) {
        unichar ch = [escaped characterAtIndex:i];
        if (ch != '\\' || i + 1 >= escaped.length) {
            [sequence appendFormat:@"%C", ch];
            continue;
        }

        unichar next = [escaped characterAtIndex:++i];
        switch (next) {
            case 'b': [sequence appendString:@"\b"]; break;
            case 'e': [sequence appendString:@"\x1b"]; break;
            case 'n': [sequence appendString:@"\n"]; break;
            case 'r': [sequence appendString:@"\r"]; break;
            case 't': [sequence appendString:@"\t"]; break;
            case 'x': {
                if (i + 2 < escaped.length) {
                    NSString *hex = [escaped substringWithRange:NSMakeRange(i + 1, 2)];
                    unsigned escapedValue = 0;
                    NSScanner *scanner = [NSScanner scannerWithString:hex];
                    if ([scanner scanHexInt:&escapedValue]) {
                        [sequence appendFormat:@"%C", (unichar)escapedValue];
                        i += 2;
                        break;
                    }
                }
                [sequence appendString:@"x"];
                break;
            }
            default:
                [sequence appendFormat:@"%C", next];
                break;
        }
    }
    return sequence;
}

static NSString *ConfiguredSequence(NSString *bindingKey, NSString *customKey, NSString *defaultSequence) {
    NSString *binding = [NSUserDefaults.standardUserDefaults stringForKey:bindingKey];
    if ([binding isEqualToString:@"DEL"]) return @"\x7f";
    if ([binding isEqualToString:@"Ctrl-H"]) return @"\b";
    if ([binding isEqualToString:@"ESC[3~"]) return @"\x1b[3~";
    if ([binding isEqualToString:@"Custom"]) {
        NSString *custom = [NSUserDefaults.standardUserDefaults stringForKey:customKey];
        NSString *sequence = SequenceFromEscapedString(custom ?: @"");
        return sequence.length > 0 ? sequence : defaultSequence;
    }
    return defaultSequence;
}

static NSString *ConfiguredBackspaceSequence(void) {
    return ConfiguredSequence(BackspaceBindingKey, BackspaceCustomSequenceKey, @"\x7f");
}

static NSString *ConfiguredDeleteSequence(void) {
    return ConfiguredSequence(DeleteBindingKey, DeleteCustomSequenceKey, @"\x1b[3~");
}

@interface TerminalTab : NSObject <PtySessionDelegate>
@property(nonatomic) PtySession *session;
@property(nonatomic) TerminalView *terminal;
@property(nonatomic) NSTabViewItem *item;
@property(nonatomic) NSFileHandle *logFileHandle;
@property(nonatomic) NSURL *logFileURL;
@property(nonatomic) NSString *host;
@property(nonatomic) NSString *username;
@property(nonatomic) int port;
@property(nonatomic) BOOL expectingPassword;
@property(nonatomic) BOOL autoPasswordSubmittedForSession;
@property(nonatomic) NSMutableString *pendingPassword;
@property(nonatomic) NSMutableString *logEscapeBuffer;
@property(nonatomic) BOOL logCollectingEscape;
@property(nonatomic) BOOL lastLogCharacterWasNewline;
@property(nonatomic) NSData *encryptedPassword;
@property(nonatomic) NSData *passwordKey;
- (void)appendText:(NSString *)text;
- (void)closeLogFile;
- (void)terminalDidSendSequence:(NSString *)sequence;
@end

@interface TerminalView : NSTextView
@property(nonatomic, weak) PtySession *session;
@property(nonatomic, weak) TerminalTab *tab;
@property(nonatomic) NSMutableArray<NSMutableString *> *terminalRows;
@property(nonatomic) NSInteger terminalRow;
@property(nonatomic) NSInteger terminalColumn;
@property(nonatomic) NSInteger savedTerminalRow;
@property(nonatomic) NSInteger savedTerminalColumn;
@property(nonatomic) NSInteger terminalColumnCount;
@property(nonatomic) NSInteger terminalRowCount;
@property(nonatomic) NSMutableString *escapeBuffer;
@property(nonatomic) BOOL collectingEscape;
- (void)updateTerminalSizeFromBounds:(BOOL)notifyRemote;
@end

@implementation TerminalView

- (instancetype)initWithFrame:(NSRect)frameRect {
    self = [super initWithFrame:frameRect];
    if (self) {
        self.terminalColumnCount = 100;
        self.terminalRowCount = 32;
        [self resetTerminalState];
    }
    return self;
}

- (BOOL)acceptsFirstResponder { return YES; }

- (void)viewDidMoveToWindow {
    [super viewDidMoveToWindow];
    [NSNotificationCenter.defaultCenter removeObserver:self name:NSViewBoundsDidChangeNotification object:nil];
    if (self.enclosingScrollView) {
        self.enclosingScrollView.contentView.postsBoundsChangedNotifications = YES;
        [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(scrollBoundsDidChange:) name:NSViewBoundsDidChangeNotification object:self.enclosingScrollView.contentView];
    }
    [self updateTerminalSizeFromBounds:YES];
}

- (void)dealloc {
    [NSNotificationCenter.defaultCenter removeObserver:self];
}

- (void)scrollBoundsDidChange:(NSNotification *)notification {
    [self updateTerminalSizeFromBounds:YES];
}

- (void)setFrameSize:(NSSize)newSize {
    [super setFrameSize:newSize];
    [self updateTerminalSizeFromBounds:YES];
}

- (void)updateTerminalSizeFromBounds:(BOOL)notifyRemote {
    NSRect visibleBounds = self.enclosingScrollView ? self.enclosingScrollView.contentView.bounds : self.bounds;
    if (visibleBounds.size.width <= 0 || visibleBounds.size.height <= 0) return;

    NSFont *font = self.font ?: [NSFont monospacedSystemFontOfSize:13 weight:NSFontWeightRegular];
    NSDictionary *attributes = @{ NSFontAttributeName: font };
    CGFloat characterWidth = MAX(1.0, ceil([@"W" sizeWithAttributes:attributes].width));
    CGFloat lineHeight = MAX(1.0, ceil(font.ascender - font.descender + font.leading));
    NSSize inset = self.textContainerInset;
    CGFloat usableWidth = MAX(0, visibleBounds.size.width - inset.width * 2.0);
    CGFloat usableHeight = MAX(0, visibleBounds.size.height - inset.height * 2.0);
    NSInteger columns = MAX(20, (NSInteger)floor(usableWidth / characterWidth));
    NSInteger rows = MAX(5, (NSInteger)floor(usableHeight / lineHeight));
    [self resizeTerminalToColumns:columns rows:rows notifyRemote:notifyRemote];
}

- (void)resizeTerminalToColumns:(NSInteger)columns rows:(NSInteger)rows notifyRemote:(BOOL)notifyRemote {
    columns = MAX(20, columns);
    rows = MAX(5, rows);
    if (columns == self.terminalColumnCount && rows == self.terminalRowCount) {
        if (notifyRemote) [self.session resizeToColumns:(int)columns rows:(int)rows];
        return;
    }

    NSArray<NSMutableString *> *oldRows = [self.terminalRows copy] ?: @[];
    self.terminalColumnCount = columns;
    self.terminalRowCount = rows;
    self.terminalRows = [NSMutableArray arrayWithCapacity:rows];
    for (NSInteger row = 0; row < rows; row++) {
        NSMutableString *newRow = [self blankTerminalRow];
        if (row < (NSInteger)oldRows.count) {
            NSString *oldRow = oldRows[(NSUInteger)row];
            NSUInteger copyLength = MIN((NSUInteger)columns, oldRow.length);
            if (copyLength > 0) {
                [newRow replaceCharactersInRange:NSMakeRange(0, copyLength) withString:[oldRow substringToIndex:copyLength]];
            }
        }
        [self.terminalRows addObject:newRow];
    }
    self.terminalRow = MIN(MAX(0, self.terminalRow), rows - 1);
    self.terminalColumn = MIN(MAX(0, self.terminalColumn), columns - 1);
    self.savedTerminalRow = MIN(MAX(0, self.savedTerminalRow), rows - 1);
    self.savedTerminalColumn = MIN(MAX(0, self.savedTerminalColumn), columns - 1);
    if (notifyRemote) [self.session resizeToColumns:(int)columns rows:(int)rows];
    [self renderTerminalScreen];
}

- (void)resetTerminalState {
    self.terminalRows = [NSMutableArray arrayWithCapacity:self.terminalRowCount];
    for (NSInteger row = 0; row < self.terminalRowCount; row++) {
        [self.terminalRows addObject:[self blankTerminalRow]];
    }
    self.terminalRow = 0;
    self.terminalColumn = 0;
    self.savedTerminalRow = 0;
    self.savedTerminalColumn = 0;
    self.escapeBuffer = [NSMutableString string];
    self.collectingEscape = NO;
    [self renderTerminalScreen];
}

- (NSMutableString *)blankTerminalRow {
    NSMutableString *row = [NSMutableString stringWithCapacity:self.terminalColumnCount];
    for (NSInteger i = 0; i < self.terminalColumnCount; i++) [row appendString:@" "];
    return row;
}

- (void)keyDown:(NSEvent *)event {
    if ((event.modifierFlags & NSEventModifierFlagCommand) != 0) {
        [super keyDown:event];
        return;
    }

    NSString *sequence = [self terminalSequenceForEvent:event];
    if (sequence.length > 0) {
        [self.session writeString:sequence];
        [self.tab terminalDidSendSequence:sequence];
    }
}

- (NSString *)terminalSequenceForEvent:(NSEvent *)event {
    NSString *chars = event.charactersIgnoringModifiers;
    unichar key = chars.length > 0 ? [chars characterAtIndex:0] : 0;

    if ((event.modifierFlags & NSEventModifierFlagControl) != 0 && key >= 'a' && key <= 'z') {
        return [NSString stringWithFormat:@"%c", key - 'a' + 1];
    }

    switch (key) {
        case NSCarriageReturnCharacter:
        case NSEnterCharacter:
        case NSNewlineCharacter:
            return @"\r";
        case NSBackspaceCharacter:
        case NSDeleteCharacter:
            return ConfiguredBackspaceSequence();
        case NSDeleteFunctionKey:
            return ConfiguredDeleteSequence();
        case NSInsertFunctionKey:
            return @"\x1b[2~";
        case NSHomeFunctionKey:
            return @"\x1b[H";
        case NSEndFunctionKey:
            return @"\x1b[F";
        case NSPageUpFunctionKey:
            return @"\x1b[5~";
        case NSPageDownFunctionKey:
            return @"\x1b[6~";
        case NSTabCharacter:
            return @"\t";
        case 0x1b:
            return @"\x1b";
        case NSUpArrowFunctionKey:
            return @"\x1b[A";
        case NSDownArrowFunctionKey:
            return @"\x1b[B";
        case NSRightArrowFunctionKey:
            return @"\x1b[C";
        case NSLeftArrowFunctionKey:
            return @"\x1b[D";
        default:
            return event.characters;
    }
}

- (void)paste:(id)sender {
    NSString *s = [NSPasteboard.generalPasteboard stringForType:NSPasteboardTypeString];
    if (s.length) {
        [self.session writeString:s];
        [self.tab terminalDidSendSequence:s];
    }
}

- (void)appendTerminalText:(NSString *)text {
    if (!text.length) return;

    for (NSUInteger i = 0; i < text.length; i++) {
        [self processTerminalCharacter:[text characterAtIndex:i]];
    }
    [self renderTerminalScreen];
}

- (void)processTerminalCharacter:(unichar)ch {
    if (self.collectingEscape) {
        [self.escapeBuffer appendFormat:@"%C", ch];
        if ([self isEscapeSequenceComplete:self.escapeBuffer]) {
            [self applyEscapeSequence:self.escapeBuffer];
            [self.escapeBuffer setString:@""];
            self.collectingEscape = NO;
        }
        return;
    }

    if (ch == 0x1b) {
        self.collectingEscape = YES;
        [self.escapeBuffer setString:@""];
        [self.escapeBuffer appendString:@"\x1b"];
        return;
    }
    if (ch == 0x9b) {
        self.collectingEscape = YES;
        [self.escapeBuffer setString:@"\x1b["];
        return;
    }
    if (ch == 0x9d) {
        self.collectingEscape = YES;
        [self.escapeBuffer setString:@"\x1b]"];
        return;
    }

    switch (ch) {
        case '\b':
        case 0x7f:
            self.terminalColumn = MAX(0, self.terminalColumn - 1);
            break;
        case '\r':
            self.terminalColumn = 0;
            break;
        case '\n':
            [self lineFeed];
            break;
        case '\t':
            self.terminalColumn = MIN(self.terminalColumnCount - 1, ((self.terminalColumn / 8) + 1) * 8);
            break;
        default:
            if (ch >= 0x20) [self writePrintableCharacter:ch];
            break;
    }
}

- (BOOL)isEscapeSequenceComplete:(NSString *)sequence {
    if (sequence.length < 2) return NO;
    unichar second = [sequence characterAtIndex:1];
    if (second == '[') {
        if (sequence.length < 3) return NO;
        unichar last = [sequence characterAtIndex:sequence.length - 1];
        return last >= 0x40 && last <= 0x7e;
    }
    if (second == ']' || second == 'P' || second == '^' || second == '_') {
        unichar last = [sequence characterAtIndex:sequence.length - 1];
        if (last == 0x07) return YES;
        if (sequence.length >= 2) {
            unichar previous = [sequence characterAtIndex:sequence.length - 2];
            return previous == 0x1b && last == '\\';
        }
        return NO;
    }
    if (second == '(' || second == ')' || second == '*' || second == '+' || second == '-' || second == '.' || second == '/') {
        return sequence.length >= 3;
    }
    return YES;
}

- (void)applyEscapeSequence:(NSString *)sequence {
    if (sequence.length < 2) return;
    unichar second = [sequence characterAtIndex:1];
    if (second == 'c') {
        [self resetTerminalState];
        return;
    }
    if (second == 'M') {
        [self reverseIndex];
        return;
    }
    if (second == 'D') {
        [self lineFeed];
        return;
    }
    if (second == 'E') {
        self.terminalColumn = 0;
        [self lineFeed];
        return;
    }
    if (second == '7' || second == 's') {
        self.savedTerminalRow = self.terminalRow;
        self.savedTerminalColumn = self.terminalColumn;
        return;
    }
    if (second == '8' || second == 'u') {
        self.terminalRow = MIN(self.terminalRowCount - 1, MAX(0, self.savedTerminalRow));
        self.terminalColumn = MIN(self.terminalColumnCount - 1, MAX(0, self.savedTerminalColumn));
        return;
    }
    if (second == ']' || second == 'P' || second == '^' || second == '_') return;
    if (second == '(' || second == ')' || second == '*' || second == '+' || second == '-' || second == '.' || second == '/') return;
    if (second != '[' || sequence.length < 3) return;

    unichar final = [sequence characterAtIndex:sequence.length - 1];
    NSString *body = [sequence substringWithRange:NSMakeRange(2, sequence.length - 3)];
    BOOL privateMode = [body hasPrefix:@"?"];
    if (privateMode) body = [body substringFromIndex:1];
    NSArray<NSNumber *> *params = [self csiParametersFromBody:body];

    if (privateMode && (final == 'h' || final == 'l')) {
        NSInteger mode = params.count ? params.firstObject.integerValue : 0;
        if (mode == 1049 || mode == 1047 || mode == 47) {
            [self clearScreen];
            self.terminalRow = 0;
            self.terminalColumn = 0;
        }
        return;
    }

    switch (final) {
        case 'A': self.terminalRow = MAX(0, self.terminalRow - [self paramAt:0 in:params defaultValue:1]); break;
        case 'B': self.terminalRow = MIN(self.terminalRowCount - 1, self.terminalRow + [self paramAt:0 in:params defaultValue:1]); break;
        case 'C': self.terminalColumn = MIN(self.terminalColumnCount - 1, self.terminalColumn + [self paramAt:0 in:params defaultValue:1]); break;
        case 'D': self.terminalColumn = MAX(0, self.terminalColumn - [self paramAt:0 in:params defaultValue:1]); break;
        case 'G': self.terminalColumn = MIN(self.terminalColumnCount - 1, MAX(0, [self paramAt:0 in:params defaultValue:1] - 1)); break;
        case 'H':
        case 'f':
            self.terminalRow = MIN(self.terminalRowCount - 1, MAX(0, [self paramAt:0 in:params defaultValue:1] - 1));
            self.terminalColumn = MIN(self.terminalColumnCount - 1, MAX(0, [self paramAt:1 in:params defaultValue:1] - 1));
            break;
        case 'J': [self eraseInDisplay:[self paramAt:0 in:params defaultValue:0]]; break;
        case 'K': [self eraseInLine:[self paramAt:0 in:params defaultValue:0]]; break;
        case 'm': break;
        default: break;
    }
}

- (NSArray<NSNumber *> *)csiParametersFromBody:(NSString *)body {
    NSMutableArray<NSNumber *> *params = [NSMutableArray array];
    NSCharacterSet *ignored = [NSCharacterSet characterSetWithCharactersInString:@"=><! "];
    NSString *cleanBody = [[body componentsSeparatedByCharactersInSet:ignored] componentsJoinedByString:@""];
    for (NSString *part in [cleanBody componentsSeparatedByString:@";"]) {
        [params addObject:@(part.length ? part.integerValue : 0)];
    }
    return params;
}

- (NSInteger)paramAt:(NSUInteger)index in:(NSArray<NSNumber *> *)params defaultValue:(NSInteger)defaultValue {
    if (index >= params.count) return defaultValue;
    NSInteger value = params[index].integerValue;
    return value == 0 ? defaultValue : value;
}

- (void)writePrintableCharacter:(unichar)ch {
    if (self.terminalRow < 0 || self.terminalRow >= self.terminalRowCount) return;
    NSMutableString *row = self.terminalRows[(NSUInteger)self.terminalRow];
    [row replaceCharactersInRange:NSMakeRange((NSUInteger)self.terminalColumn, 1) withString:[NSString stringWithFormat:@"%C", ch]];
    self.terminalColumn++;
    if (self.terminalColumn >= self.terminalColumnCount) {
        self.terminalColumn = 0;
        [self lineFeed];
    }
}

- (void)lineFeed {
    if (self.terminalRow >= self.terminalRowCount - 1) {
        [self.terminalRows removeObjectAtIndex:0];
        [self.terminalRows addObject:[self blankTerminalRow]];
    } else {
        self.terminalRow++;
    }
}

- (void)reverseIndex {
    if (self.terminalRow <= 0) {
        [self.terminalRows removeLastObject];
        [self.terminalRows insertObject:[self blankTerminalRow] atIndex:0];
    } else {
        self.terminalRow--;
    }
}

- (void)clearScreen {
    for (NSInteger row = 0; row < self.terminalRowCount; row++) {
        self.terminalRows[(NSUInteger)row] = [self blankTerminalRow];
    }
}

- (void)eraseInDisplay:(NSInteger)mode {
    if (mode == 2 || mode == 3) {
        [self clearScreen];
        return;
    }
    if (mode == 1) {
        for (NSInteger row = 0; row < self.terminalRow; row++) self.terminalRows[(NSUInteger)row] = [self blankTerminalRow];
        [self eraseInLine:1];
        return;
    }
    [self eraseInLine:0];
    for (NSInteger row = self.terminalRow + 1; row < self.terminalRowCount; row++) self.terminalRows[(NSUInteger)row] = [self blankTerminalRow];
}

- (void)eraseInLine:(NSInteger)mode {
    if (self.terminalRow < 0 || self.terminalRow >= self.terminalRowCount) return;
    NSMutableString *row = self.terminalRows[(NSUInteger)self.terminalRow];
    NSInteger start = mode == 1 ? 0 : self.terminalColumn;
    NSInteger end = mode == 0 ? self.terminalColumnCount - 1 : self.terminalColumn;
    if (mode == 2) { start = 0; end = self.terminalColumnCount - 1; }
    for (NSInteger column = start; column <= end; column++) {
        [row replaceCharactersInRange:NSMakeRange((NSUInteger)column, 1) withString:@" "];
    }
}

- (void)renderTerminalScreen {
    NSMutableString *screen = [NSMutableString string];
    for (NSInteger row = 0; row < self.terminalRowCount; row++) {
        [screen appendString:self.terminalRows[(NSUInteger)row]];
        if (row < self.terminalRowCount - 1) [screen appendString:@"\n"];
    }
    NSAttributedString *attr = [[NSAttributedString alloc] initWithString:screen attributes:@{NSFontAttributeName:self.font ?: [NSFont monospacedSystemFontOfSize:13 weight:NSFontWeightRegular]}];
    [self.textStorage setAttributedString:attr];
    NSUInteger cursor = (NSUInteger)(self.terminalRow * (self.terminalColumnCount + 1) + self.terminalColumn);
    [self setSelectedRange:NSMakeRange(MIN(cursor, self.string.length), 0)];
    [self scrollRangeToVisible:self.selectedRange];
}
@end

@implementation TerminalTab
- (void)appendText:(NSString *)text {
    if (!text.length) return;

    [self.terminal appendTerminalText:text];
    NSString *plainLogText = [self plainLogTextFromTerminalText:text];
    NSData *data = [plainLogText dataUsingEncoding:NSUTF8StringEncoding];
    if (data.length > 0) {
        [self.logFileHandle writeData:data];
    }
    [self handlePasswordPromptInText:text];
}

- (NSString *)plainLogTextFromTerminalText:(NSString *)text {
    NSMutableString *output = [NSMutableString string];
    if (!self.logEscapeBuffer) self.logEscapeBuffer = [NSMutableString string];

    for (NSUInteger i = 0; i < text.length; i++) {
        unichar ch = [text characterAtIndex:i];
        if (self.logCollectingEscape) {
            [self.logEscapeBuffer appendFormat:@"%C", ch];
            if ([self isLogEscapeSequenceComplete:self.logEscapeBuffer]) {
                [self.logEscapeBuffer setString:@""];
                self.logCollectingEscape = NO;
            }
            continue;
        }

        if (ch == 0x1b) {
            self.logCollectingEscape = YES;
            [self.logEscapeBuffer setString:@"\x1b"];
            continue;
        }
        if (ch == 0x9b) {
            self.logCollectingEscape = YES;
            [self.logEscapeBuffer setString:@"\x1b["];
            continue;
        }
        if (ch == 0x9d) {
            self.logCollectingEscape = YES;
            [self.logEscapeBuffer setString:@"\x1b]"];
            continue;
        }

        if (ch == '\r' || ch == '\n') {
            if (!self.lastLogCharacterWasNewline) {
                [output appendString:@"\n"];
                self.lastLogCharacterWasNewline = YES;
            }
            continue;
        }
        if (ch == '\b' || ch == 0x7f) {
            if (output.length > 0) [output deleteCharactersInRange:NSMakeRange(output.length - 1, 1)];
            continue;
        }
        if (ch == '\t') {
            [output appendString:@"\t"];
            self.lastLogCharacterWasNewline = NO;
            continue;
        }
        if (ch >= 0x20) {
            [output appendFormat:@"%C", ch];
            self.lastLogCharacterWasNewline = NO;
        }
    }
    return output;
}

- (BOOL)isLogEscapeSequenceComplete:(NSString *)sequence {
    if (sequence.length < 2) return NO;
    unichar second = [sequence characterAtIndex:1];
    if (second == '[') {
        if (sequence.length < 3) return NO;
        unichar last = [sequence characterAtIndex:sequence.length - 1];
        return last >= 0x40 && last <= 0x7e;
    }
    if (second == ']' || second == 'P' || second == '^' || second == '_') {
        unichar last = [sequence characterAtIndex:sequence.length - 1];
        if (last == 0x07) return YES;
        if (sequence.length >= 2) {
            unichar previous = [sequence characterAtIndex:sequence.length - 2];
            return previous == 0x1b && last == '\\';
        }
        return NO;
    }
    if (second == '(' || second == ')' || second == '*' || second == '+' || second == '-' || second == '.' || second == '/') {
        return sequence.length >= 3;
    }
    return YES;
}

- (void)handlePasswordPromptInText:(NSString *)text {
    NSString *lowercase = text.lowercaseString;
    if ([lowercase rangeOfString:@"password:"].location == NSNotFound) return;

    NSString *password = [self decryptedPassword];
    if (password.length > 0 && !self.autoPasswordSubmittedForSession) {
        self.autoPasswordSubmittedForSession = YES;
        [self.session writeString:[password stringByAppendingString:@"\r"]];
        return;
    }

    self.expectingPassword = YES;
    self.pendingPassword = [NSMutableString string];
}

- (void)terminalDidSendSequence:(NSString *)sequence {
    if (!self.expectingPassword) return;

    if ([sequence isEqualToString:@"\r"] || [sequence isEqualToString:@"\n"]) {
        [self storePassword:self.pendingPassword ?: @""];
        self.pendingPassword = nil;
        self.expectingPassword = NO;
        return;
    }
    if ([sequence isEqualToString:@"\b"] || [sequence isEqualToString:@"\x7f"]) {
        if (self.pendingPassword.length > 0) {
            [self.pendingPassword deleteCharactersInRange:NSMakeRange(self.pendingPassword.length - 1, 1)];
        }
        return;
    }
    if (sequence.length == 1 && [sequence characterAtIndex:0] >= 0x20) {
        [self.pendingPassword appendString:sequence];
    }
}

- (void)storePassword:(NSString *)password {
    NSData *plainData = [password dataUsingEncoding:NSUTF8StringEncoding];
    if (plainData.length == 0) return;

    NSMutableData *key = [NSMutableData dataWithLength:plainData.length];
    arc4random_buf(key.mutableBytes, key.length);

    NSMutableData *encrypted = [NSMutableData dataWithLength:plainData.length];
    const uint8_t *plainBytes = (const uint8_t *)plainData.bytes;
    const uint8_t *keyBytes = (const uint8_t *)key.bytes;
    uint8_t *encryptedBytes = (uint8_t *)encrypted.mutableBytes;
    for (NSUInteger i = 0; i < plainData.length; i++) {
        encryptedBytes[i] = plainBytes[i] ^ keyBytes[i];
    }
    self.passwordKey = key;
    self.encryptedPassword = encrypted;
}

- (NSString *)decryptedPassword {
    if (self.encryptedPassword.length == 0 || self.encryptedPassword.length != self.passwordKey.length) return nil;

    NSMutableData *plain = [NSMutableData dataWithLength:self.encryptedPassword.length];
    const uint8_t *encryptedBytes = (const uint8_t *)self.encryptedPassword.bytes;
    const uint8_t *keyBytes = (const uint8_t *)self.passwordKey.bytes;
    uint8_t *plainBytes = (uint8_t *)plain.mutableBytes;
    for (NSUInteger i = 0; i < self.encryptedPassword.length; i++) {
        plainBytes[i] = encryptedBytes[i] ^ keyBytes[i];
    }
    return [[NSString alloc] initWithData:plain encoding:NSUTF8StringEncoding];
}

- (void)closeLogFile {
    if (!self.logFileHandle) return;

    [self.logFileHandle synchronizeFile];
    [self.logFileHandle closeFile];
    self.logFileHandle = nil;
}

- (void)ptySession:(PtySession *)session didReceiveText:(NSString *)text {
    [self appendText:text];
}
- (void)ptySessionDidExit:(PtySession *)session status:(int)status {
    [self appendText:[NSString stringWithFormat:@"\n[ssh exited: %d]\n", status]];
    [self closeLogFile];
    [NSNotificationCenter.defaultCenter postNotificationName:ActiveSessionsChangedNotification object:self];
}
- (void)dealloc {
    [self closeLogFile];
}
@end

@interface AppDelegate () <NSTableViewDataSource, NSTableViewDelegate>
@property(nonatomic) NSWindow *window;
@property(nonatomic) NSTabView *tabView;
@property(nonatomic) NSWindow *sessionManagerWindow;
@property(nonatomic) NSTableView *sessionTableView;
@property(nonatomic) NSWindow *activeSessionsWindow;
@property(nonatomic) NSTableView *activeSessionsTableView;
@property(nonatomic) NSWindow *logManagerWindow;
@property(nonatomic) NSTableView *logTableView;
@property(nonatomic) NSMutableArray<NSURL *> *logFileURLs;
@property(nonatomic) NSMenuItem *clearLogsOnExitMenuItem;
@property(nonatomic) NSWindow *keyBindingWindow;
@property(nonatomic) NSPopUpButton *backspaceBindingPopUp;
@property(nonatomic) NSTextField *customBackspaceField;
@property(nonatomic) NSPopUpButton *deleteBindingPopUp;
@property(nonatomic) NSTextField *customDeleteField;
@property(nonatomic) NSMenu *recentMenu;
@property(nonatomic) NSMutableArray<TerminalTab *> *tabs;
@property(nonatomic) NSMutableArray<NSDictionary *> *recentSessions;
@end

@implementation AppDelegate
- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    if ([NSWindow respondsToSelector:@selector(setAllowsAutomaticWindowTabbing:)]) {
        NSWindow.allowsAutomaticWindowTabbing = NO;
    }
    [NSUserDefaults.standardUserDefaults registerDefaults:@{
        BackspaceBindingKey: @"Custom",
        BackspaceCustomSequenceKey: @"127",
        DeleteBindingKey: @"ESC[3~",
        ClearLogsOnExitKey: @YES
    }];
    self.tabs = [NSMutableArray array];
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(activeSessionsChanged:) name:ActiveSessionsChangedNotification object:nil];
    [self loadRecentSessions];
    [self buildMenu];
    [self createMainWindow];
    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (BOOL)applicationShouldHandleReopen:(NSApplication *)sender hasVisibleWindows:(BOOL)flag {
    if (!flag) {
        [self createMainWindow];
        [self.window makeKeyAndOrderFront:nil];
    }
    return YES;
}

- (void)buildMenu {
    NSMenu *mainMenu = [[NSMenu alloc] initWithTitle:@""];
    NSMenuItem *appItem = [[NSMenuItem alloc] initWithTitle:@"MacSshClient" action:nil keyEquivalent:@""];
    [mainMenu addItem:appItem];
    NSMenu *appMenu = [[NSMenu alloc] initWithTitle:@"MacSshClient"];
    [appMenu addItemWithTitle:@"About MacSshClient" action:@selector(orderFrontStandardAboutPanel:) keyEquivalent:@""];
    [appMenu addItem:[NSMenuItem separatorItem]];
    [appMenu addItemWithTitle:@"Quit MacSshClient" action:@selector(terminate:) keyEquivalent:@"q"];
    appItem.submenu = appMenu;

    NSMenuItem *fileItem = [[NSMenuItem alloc] initWithTitle:@"File" action:nil keyEquivalent:@""];
    [mainMenu addItem:fileItem];
    NSMenu *fileMenu = [[NSMenu alloc] initWithTitle:@"File"];
    [fileMenu addItemWithTitle:@"New SSH Connection..." action:@selector(newConnection:) keyEquivalent:@"n"].target = self;
    [fileMenu addItemWithTitle:@"Reconnect" action:@selector(reconnectCurrent:) keyEquivalent:@"r"].target = self;
    [fileMenu addItemWithTitle:@"Close Tab" action:@selector(closeCurrentTab:) keyEquivalent:@"w"].target = self;
    [fileMenu addItem:[NSMenuItem separatorItem]];
    [fileMenu addItemWithTitle:@"Disconnect" action:@selector(disconnectCurrent:) keyEquivalent:@"d"].target = self;
    fileItem.submenu = fileMenu;

    NSMenuItem *editItem = [[NSMenuItem alloc] initWithTitle:@"Edit" action:nil keyEquivalent:@""];
    [mainMenu addItem:editItem];
    NSMenu *editMenu = [[NSMenu alloc] initWithTitle:@"Edit"];
    [editMenu addItemWithTitle:@"Copy" action:@selector(copy:) keyEquivalent:@"c"];
    [editMenu addItemWithTitle:@"Paste" action:@selector(paste:) keyEquivalent:@"v"];
    [editMenu addItemWithTitle:@"Select All" action:@selector(selectAll:) keyEquivalent:@"a"];
    editItem.submenu = editMenu;

    NSMenuItem *recentItem = [[NSMenuItem alloc] initWithTitle:@"Sessions" action:nil keyEquivalent:@""];
    [mainMenu addItem:recentItem];
    self.recentMenu = [[NSMenu alloc] initWithTitle:@"Sessions"];
    recentItem.submenu = self.recentMenu;
    [self rebuildRecentMenu];

    NSMenuItem *viewItem = [[NSMenuItem alloc] initWithTitle:@"View" action:nil keyEquivalent:@""];
    [mainMenu addItem:viewItem];
    NSMenu *viewMenu = [[NSMenu alloc] initWithTitle:@"View"];
    [viewMenu addItemWithTitle:@"Increase Font Size" action:@selector(increaseFont:) keyEquivalent:@"+"] .target = self;
    [viewMenu addItemWithTitle:@"Decrease Font Size" action:@selector(decreaseFont:) keyEquivalent:@"-"] .target = self;
    viewItem.submenu = viewMenu;

    NSMenuItem *terminalItem = [[NSMenuItem alloc] initWithTitle:@"Terminal" action:nil keyEquivalent:@""];
    [mainMenu addItem:terminalItem];
    NSMenu *terminalMenu = [[NSMenu alloc] initWithTitle:@"Terminal"];
    [terminalMenu addItemWithTitle:@"Key Bindings..." action:@selector(showKeyBindings:) keyEquivalent:@""] .target = self;
    terminalItem.submenu = terminalMenu;

    NSMenuItem *logItem = [[NSMenuItem alloc] initWithTitle:@"Log Management" action:nil keyEquivalent:@""];
    [mainMenu addItem:logItem];
    NSMenu *logMenu = [[NSMenu alloc] initWithTitle:@"Log Management"];
    [logMenu addItemWithTitle:@"Manage Logs..." action:@selector(showLogManager:) keyEquivalent:@""] .target = self;
    self.clearLogsOnExitMenuItem = [logMenu addItemWithTitle:@"Clear Logs on Exit" action:@selector(toggleClearLogsOnExit:) keyEquivalent:@""];
    self.clearLogsOnExitMenuItem.target = self;
    [self updateClearLogsOnExitMenuItem];
    logItem.submenu = logMenu;

    NSApp.mainMenu = mainMenu;
}

- (void)createMainWindow {
    if (self.window) return;
    NSRect frame = NSMakeRect(100, 100, 1000, 700);
    self.window = [[NSWindow alloc] initWithContentRect:frame styleMask:(NSWindowStyleMaskTitled|NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable|NSWindowStyleMaskResizable) backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"MacSshClient - SSH Client";
    self.window.releasedWhenClosed = NO;
    self.window.tabbingMode = NSWindowTabbingModeDisallowed;
    self.tabView = [[NSTabView alloc] initWithFrame:self.window.contentView.bounds];
    self.tabView.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    [self.window.contentView addSubview:self.tabView];

    [self showWelcomeTabIfNeeded];
}

- (void)showWelcomeTabIfNeeded {
    if (self.tabView.numberOfTabViewItems > 0) return;

    NSTextField *welcome = [NSTextField labelWithString:@"Use File > New SSH Connection... to open an SSH tab. Passwords and passphrases are never saved."];
    welcome.alignment = NSTextAlignmentCenter;
    welcome.frame = NSMakeRect(20, 320, 960, 40);
    NSView *welcomeView = [[NSView alloc] initWithFrame:self.tabView.bounds];
    welcome.autoresizingMask = NSViewWidthSizable | NSViewMinYMargin | NSViewMaxYMargin;
    [welcomeView addSubview:welcome];
    NSTabViewItem *item = [[NSTabViewItem alloc] initWithIdentifier:@"welcome"];
    item.label = @"Welcome";
    item.view = welcomeView;
    [self.tabView addTabViewItem:item];
}

- (void)newConnection:(id)sender {
    [self showConnectionDialogWithSession:nil];
}

- (void)showKeyBindings:(id)sender {
    if (!self.keyBindingWindow) {
        NSRect frame = NSMakeRect(220, 220, 560, 230);
        self.keyBindingWindow = [[NSWindow alloc] initWithContentRect:frame styleMask:(NSWindowStyleMaskTitled|NSWindowStyleMaskClosable) backing:NSBackingStoreBuffered defer:NO];
        self.keyBindingWindow.title = @"Key Bindings";
        self.keyBindingWindow.releasedWhenClosed = NO;

        NSView *contentView = self.keyBindingWindow.contentView;
        NSTextField *backspaceLabel = [NSTextField labelWithString:@"Backspace sends:"];
        backspaceLabel.frame = NSMakeRect(20, 172, 120, 24);
        [contentView addSubview:backspaceLabel];

        self.backspaceBindingPopUp = [[NSPopUpButton alloc] initWithFrame:NSMakeRect(150, 170, 150, 28) pullsDown:NO];
        [self.backspaceBindingPopUp addItemsWithTitles:@[@"DEL (0x7F)", @"Ctrl-H (0x08)", @"Custom"]];
        [self.backspaceBindingPopUp setTarget:self];
        [self.backspaceBindingPopUp setAction:@selector(keyBindingSelectionChanged:)];
        [contentView addSubview:self.backspaceBindingPopUp];

        self.customBackspaceField = [[NSTextField alloc] initWithFrame:NSMakeRect(314, 172, 220, 24)];
        self.customBackspaceField.placeholderString = @"e.g. 0x7f, 127, ^H, \\b";
        [contentView addSubview:self.customBackspaceField];

        NSTextField *deleteLabel = [NSTextField labelWithString:@"Delete sends:"];
        deleteLabel.frame = NSMakeRect(20, 128, 120, 24);
        [contentView addSubview:deleteLabel];

        self.deleteBindingPopUp = [[NSPopUpButton alloc] initWithFrame:NSMakeRect(150, 126, 150, 28) pullsDown:NO];
        [self.deleteBindingPopUp addItemsWithTitles:@[@"ESC [ 3 ~", @"DEL (0x7F)", @"Ctrl-H (0x08)", @"Custom"]];
        [self.deleteBindingPopUp setTarget:self];
        [self.deleteBindingPopUp setAction:@selector(keyBindingSelectionChanged:)];
        [contentView addSubview:self.deleteBindingPopUp];

        self.customDeleteField = [[NSTextField alloc] initWithFrame:NSMakeRect(314, 128, 220, 24)];
        self.customDeleteField.placeholderString = @"e.g. ESC[3~, \\e[3~, 0x7f";
        [contentView addSubview:self.customDeleteField];

        NSTextField *hint = [NSTextField labelWithString:@"Custom values accept plain text, decimal, 0x hex, ^A notation, ESC..., and escapes like \\x7f or \\e[3~."];
        hint.frame = NSMakeRect(20, 78, 514, 34);
        hint.lineBreakMode = NSLineBreakByWordWrapping;
        hint.textColor = NSColor.secondaryLabelColor;
        [contentView addSubview:hint];

        NSTextField *applyHint = [NSTextField labelWithString:@"Changes apply to the next key press in any active SSH session."];
        applyHint.frame = NSMakeRect(20, 52, 514, 20);
        applyHint.textColor = NSColor.secondaryLabelColor;
        [contentView addSubview:applyHint];

        NSButton *saveButton = [NSButton buttonWithTitle:@"Save" target:self action:@selector(saveKeyBindings:)];
        saveButton.frame = NSMakeRect(364, 14, 80, 28);
        [contentView addSubview:saveButton];

        NSButton *resetButton = [NSButton buttonWithTitle:@"Reset" target:self action:@selector(resetKeyBindings:)];
        resetButton.frame = NSMakeRect(454, 14, 80, 28);
        [contentView addSubview:resetButton];
    }

    [self loadKeyBindingControls];
    [self.keyBindingWindow makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (void)loadKeyBindingControls {
    [self selectBinding:[NSUserDefaults.standardUserDefaults stringForKey:BackspaceBindingKey] ?: @"DEL" inPopUp:self.backspaceBindingPopUp defaultTitle:@"DEL (0x7F)"];
    [self selectBinding:[NSUserDefaults.standardUserDefaults stringForKey:DeleteBindingKey] ?: @"ESC[3~" inPopUp:self.deleteBindingPopUp defaultTitle:@"ESC [ 3 ~"];
    self.customBackspaceField.stringValue = [NSUserDefaults.standardUserDefaults stringForKey:BackspaceCustomSequenceKey] ?: @"";
    self.customDeleteField.stringValue = [NSUserDefaults.standardUserDefaults stringForKey:DeleteCustomSequenceKey] ?: @"";
    [self keyBindingSelectionChanged:nil];
}

- (void)selectBinding:(NSString *)binding inPopUp:(NSPopUpButton *)popUp defaultTitle:(NSString *)defaultTitle {
    if ([binding isEqualToString:@"Ctrl-H"]) {
        [popUp selectItemWithTitle:@"Ctrl-H (0x08)"];
    } else if ([binding isEqualToString:@"DEL"]) {
        [popUp selectItemWithTitle:@"DEL (0x7F)"];
    } else if ([binding isEqualToString:@"ESC[3~"]) {
        [popUp selectItemWithTitle:@"ESC [ 3 ~"];
    } else if ([binding isEqualToString:@"Custom"]) {
        [popUp selectItemWithTitle:@"Custom"];
    } else {
        [popUp selectItemWithTitle:defaultTitle];
    }
}

- (void)keyBindingSelectionChanged:(id)sender {
    self.customBackspaceField.enabled = [self.backspaceBindingPopUp.titleOfSelectedItem isEqualToString:@"Custom"];
    self.customDeleteField.enabled = [self.deleteBindingPopUp.titleOfSelectedItem isEqualToString:@"Custom"];
}

- (NSString *)bindingValueForSelectedTitle:(NSString *)selected defaultValue:(NSString *)defaultValue {
    if ([selected isEqualToString:@"Ctrl-H (0x08)"]) return @"Ctrl-H";
    if ([selected isEqualToString:@"DEL (0x7F)"]) return @"DEL";
    if ([selected isEqualToString:@"ESC [ 3 ~"]) return @"ESC[3~";
    if ([selected isEqualToString:@"Custom"]) return @"Custom";
    return defaultValue;
}

- (void)saveKeyBindings:(id)sender {
    NSString *backspaceBinding = [self bindingValueForSelectedTitle:self.backspaceBindingPopUp.titleOfSelectedItem defaultValue:@"DEL"];
    NSString *deleteBinding = [self bindingValueForSelectedTitle:self.deleteBindingPopUp.titleOfSelectedItem defaultValue:@"ESC[3~"];

    [NSUserDefaults.standardUserDefaults setObject:backspaceBinding forKey:BackspaceBindingKey];
    [NSUserDefaults.standardUserDefaults setObject:deleteBinding forKey:DeleteBindingKey];
    [NSUserDefaults.standardUserDefaults setObject:self.customBackspaceField.stringValue ?: @"" forKey:BackspaceCustomSequenceKey];
    [NSUserDefaults.standardUserDefaults setObject:self.customDeleteField.stringValue ?: @"" forKey:DeleteCustomSequenceKey];
}

- (void)resetKeyBindings:(id)sender {
    [NSUserDefaults.standardUserDefaults removeObjectForKey:BackspaceBindingKey];
    [NSUserDefaults.standardUserDefaults removeObjectForKey:BackspaceCustomSequenceKey];
    [NSUserDefaults.standardUserDefaults removeObjectForKey:DeleteBindingKey];
    [NSUserDefaults.standardUserDefaults removeObjectForKey:DeleteCustomSequenceKey];
    [self loadKeyBindingControls];
}

- (void)showActiveSessions:(id)sender {
    if (!self.activeSessionsWindow) {
        NSRect frame = NSMakeRect(180, 180, 760, 420);
        self.activeSessionsWindow = [[NSWindow alloc] initWithContentRect:frame styleMask:(NSWindowStyleMaskTitled|NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable|NSWindowStyleMaskResizable) backing:NSBackingStoreBuffered defer:NO];
        self.activeSessionsWindow.title = @"Active SSH Sessions";
        self.activeSessionsWindow.releasedWhenClosed = NO;

        NSView *contentView = self.activeSessionsWindow.contentView;
        NSScrollView *scrollView = [[NSScrollView alloc] initWithFrame:NSMakeRect(16, 56, 728, 348)];
        scrollView.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        scrollView.hasVerticalScroller = YES;

        self.activeSessionsTableView = [[NSTableView alloc] initWithFrame:scrollView.bounds];
        self.activeSessionsTableView.delegate = self;
        self.activeSessionsTableView.dataSource = self;
        self.activeSessionsTableView.allowsMultipleSelection = NO;
        self.activeSessionsTableView.usesAlternatingRowBackgroundColors = YES;
        self.activeSessionsTableView.doubleAction = @selector(selectActiveSessionTab:);
        self.activeSessionsTableView.target = self;

        NSDictionary<NSString *, NSNumber *> *columns = @{ @"status": @110, @"session": @280, @"host": @170, @"port": @70, @"tab": @90 };
        NSDictionary<NSString *, NSString *> *titles = @{ @"status": @"Status", @"session": @"Session", @"host": @"Host", @"port": @"Port", @"tab": @"Tab" };
        for (NSString *identifier in @[@"status", @"session", @"host", @"port", @"tab"]) {
            NSTableColumn *column = [[NSTableColumn alloc] initWithIdentifier:identifier];
            column.title = titles[identifier];
            column.width = columns[identifier].doubleValue;
            [self.activeSessionsTableView addTableColumn:column];
        }

        scrollView.documentView = self.activeSessionsTableView;
        [contentView addSubview:scrollView];

        NSButton *selectButton = [NSButton buttonWithTitle:@"Select Tab" target:self action:@selector(selectActiveSessionTab:)];
        selectButton.frame = NSMakeRect(16, 16, 90, 28);
        selectButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:selectButton];

        NSButton *reconnectButton = [NSButton buttonWithTitle:@"Reconnect" target:self action:@selector(reconnectSelectedActiveSession:)];
        reconnectButton.frame = NSMakeRect(114, 16, 100, 28);
        reconnectButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:reconnectButton];

        NSButton *disconnectButton = [NSButton buttonWithTitle:@"Disconnect" target:self action:@selector(disconnectSelectedActiveSession:)];
        disconnectButton.frame = NSMakeRect(222, 16, 100, 28);
        disconnectButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:disconnectButton];

        NSButton *closeButton = [NSButton buttonWithTitle:@"Close" target:self action:@selector(closeActiveSessionsWindow:)];
        closeButton.frame = NSMakeRect(654, 16, 90, 28);
        closeButton.autoresizingMask = NSViewMinXMargin | NSViewMaxYMargin;
        [contentView addSubview:closeButton];
    }

    [self.activeSessionsTableView reloadData];
    [self.activeSessionsWindow makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (void)closeActiveSessionsWindow:(id)sender {
    [self.activeSessionsWindow orderOut:nil];
}

- (TerminalTab *)selectedActiveSessionTab {
    NSInteger row = self.activeSessionsTableView.selectedRow;
    if (row < 0 || row >= (NSInteger)self.tabs.count) return nil;
    return self.tabs[(NSUInteger)row];
}

- (void)selectActiveSessionTab:(id)sender {
    TerminalTab *tab = [self selectedActiveSessionTab];
    if (!tab) return;
    [self.tabView selectTabViewItem:tab.item];
    [self.window makeKeyAndOrderFront:nil];
    [self.window makeFirstResponder:tab.terminal];
}

- (void)reconnectSelectedActiveSession:(id)sender {
    TerminalTab *tab = [self selectedActiveSessionTab];
    if (!tab) return;
    [self reconnectTab:tab];
}

- (void)disconnectSelectedActiveSession:(id)sender {
    TerminalTab *tab = [self selectedActiveSessionTab];
    if (!tab) return;
    [tab.session close];
    [self.activeSessionsTableView reloadData];
}

- (void)activeSessionsChanged:(NSNotification *)notification {
    [self.activeSessionsTableView reloadData];
}

- (void)showLogManager:(id)sender {
    if (!self.logManagerWindow) {
        NSRect frame = NSMakeRect(180, 180, 760, 440);
        self.logManagerWindow = [[NSWindow alloc] initWithContentRect:frame styleMask:(NSWindowStyleMaskTitled|NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable|NSWindowStyleMaskResizable) backing:NSBackingStoreBuffered defer:NO];
        self.logManagerWindow.title = @"SSH Session Logs";
        self.logManagerWindow.releasedWhenClosed = NO;

        NSView *contentView = self.logManagerWindow.contentView;
        NSScrollView *scrollView = [[NSScrollView alloc] initWithFrame:NSMakeRect(16, 56, 728, 368)];
        scrollView.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        scrollView.hasVerticalScroller = YES;

        self.logTableView = [[NSTableView alloc] initWithFrame:scrollView.bounds];
        self.logTableView.delegate = self;
        self.logTableView.dataSource = self;
        self.logTableView.allowsMultipleSelection = YES;
        self.logTableView.usesAlternatingRowBackgroundColors = YES;
        self.logTableView.doubleAction = @selector(revealSelectedLog:);
        self.logTableView.target = self;

        NSDictionary<NSString *, NSNumber *> *columns = @{ @"name": @420, @"modified": @180, @"size": @100 };
        NSDictionary<NSString *, NSString *> *titles = @{ @"name": @"Log File", @"modified": @"Modified", @"size": @"Size" };
        for (NSString *identifier in @[@"name", @"modified", @"size"]) {
            NSTableColumn *column = [[NSTableColumn alloc] initWithIdentifier:identifier];
            column.title = titles[identifier];
            column.width = columns[identifier].doubleValue;
            [self.logTableView addTableColumn:column];
        }

        scrollView.documentView = self.logTableView;
        [contentView addSubview:scrollView];

        NSButton *revealButton = [NSButton buttonWithTitle:@"Reveal" target:self action:@selector(revealSelectedLog:)];
        revealButton.frame = NSMakeRect(16, 16, 90, 28);
        revealButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:revealButton];

        NSButton *deleteButton = [NSButton buttonWithTitle:@"Delete Selected" target:self action:@selector(deleteSelectedLogs:)];
        deleteButton.frame = NSMakeRect(114, 16, 130, 28);
        deleteButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:deleteButton];

        NSButton *refreshButton = [NSButton buttonWithTitle:@"Refresh" target:self action:@selector(refreshLogManager:)];
        refreshButton.frame = NSMakeRect(252, 16, 90, 28);
        refreshButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:refreshButton];

        NSButton *closeButton = [NSButton buttonWithTitle:@"Close" target:self action:@selector(closeLogManager:)];
        closeButton.frame = NSMakeRect(654, 16, 90, 28);
        closeButton.autoresizingMask = NSViewMinXMargin | NSViewMaxYMargin;
        [contentView addSubview:closeButton];
    }

    [self refreshLogFiles];
    [self.logManagerWindow makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (void)closeLogManager:(id)sender {
    [self.logManagerWindow orderOut:nil];
}

- (void)refreshLogManager:(id)sender {
    [self refreshLogFiles];
}

- (void)revealSelectedLog:(id)sender {
    NSInteger row = self.logTableView.selectedRow;
    if (row < 0 || row >= (NSInteger)self.logFileURLs.count) return;

    [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[self.logFileURLs[(NSUInteger)row]]];
}

- (void)deleteSelectedLogs:(id)sender {
    NSIndexSet *selectedRows = self.logTableView.selectedRowIndexes;
    if (selectedRows.count == 0) return;

    [selectedRows enumerateIndexesWithOptions:NSEnumerationReverse usingBlock:^(NSUInteger idx, BOOL *stop) {
        if (idx < self.logFileURLs.count) {
            [NSFileManager.defaultManager removeItemAtURL:self.logFileURLs[idx] error:nil];
            [self.logFileURLs removeObjectAtIndex:idx];
        }
    }];
    [self.logTableView reloadData];
}

- (void)toggleClearLogsOnExit:(id)sender {
    BOOL clearOnExit = ![NSUserDefaults.standardUserDefaults boolForKey:ClearLogsOnExitKey];
    [NSUserDefaults.standardUserDefaults setBool:clearOnExit forKey:ClearLogsOnExitKey];
    [self updateClearLogsOnExitMenuItem];
}

- (void)updateClearLogsOnExitMenuItem {
    self.clearLogsOnExitMenuItem.state = [NSUserDefaults.standardUserDefaults boolForKey:ClearLogsOnExitKey] ? NSControlStateValueOn : NSControlStateValueOff;
}

- (void)showSessionManager:(id)sender {
    if (!self.sessionManagerWindow) {
        NSRect frame = NSMakeRect(160, 160, 720, 420);
        self.sessionManagerWindow = [[NSWindow alloc] initWithContentRect:frame styleMask:(NSWindowStyleMaskTitled|NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable|NSWindowStyleMaskResizable) backing:NSBackingStoreBuffered defer:NO];
        self.sessionManagerWindow.title = @"Session Manager";
        self.sessionManagerWindow.releasedWhenClosed = NO;

        NSView *contentView = self.sessionManagerWindow.contentView;
        NSScrollView *scrollView = [[NSScrollView alloc] initWithFrame:NSMakeRect(16, 56, 688, 348)];
        scrollView.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        scrollView.hasVerticalScroller = YES;

        self.sessionTableView = [[NSTableView alloc] initWithFrame:scrollView.bounds];
        self.sessionTableView.delegate = self;
        self.sessionTableView.dataSource = self;
        self.sessionTableView.allowsMultipleSelection = YES;
        self.sessionTableView.usesAlternatingRowBackgroundColors = YES;
        self.sessionTableView.doubleAction = @selector(connectSelectedSession:);
        self.sessionTableView.target = self;

        NSDictionary<NSString *, NSNumber *> *columns = @{ @"username": @140, @"host": @260, @"port": @70, @"lastUsed": @190 };
        NSDictionary<NSString *, NSString *> *titles = @{ @"username": @"Username", @"host": @"Host", @"port": @"Port", @"lastUsed": @"Last Used" };
        for (NSString *identifier in @[@"username", @"host", @"port", @"lastUsed"]) {
            NSTableColumn *column = [[NSTableColumn alloc] initWithIdentifier:identifier];
            column.title = titles[identifier];
            column.width = columns[identifier].doubleValue;
            [self.sessionTableView addTableColumn:column];
        }

        scrollView.documentView = self.sessionTableView;
        [contentView addSubview:scrollView];

        NSButton *connectButton = [NSButton buttonWithTitle:@"Connect" target:self action:@selector(connectSelectedSession:)];
        connectButton.frame = NSMakeRect(16, 16, 90, 28);
        connectButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:connectButton];

        NSButton *deleteButton = [NSButton buttonWithTitle:@"Delete Selected" target:self action:@selector(deleteSelectedSessions:)];
        deleteButton.frame = NSMakeRect(114, 16, 130, 28);
        deleteButton.autoresizingMask = NSViewMaxXMargin | NSViewMaxYMargin;
        [contentView addSubview:deleteButton];

        NSButton *closeButton = [NSButton buttonWithTitle:@"Close" target:self action:@selector(closeSessionManager:)];
        closeButton.frame = NSMakeRect(614, 16, 90, 28);
        closeButton.autoresizingMask = NSViewMinXMargin | NSViewMaxYMargin;
        [contentView addSubview:closeButton];
    }

    [self.sessionTableView reloadData];
    [self.sessionManagerWindow makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (void)closeSessionManager:(id)sender {
    [self.sessionManagerWindow orderOut:nil];
}

- (void)connectSelectedSession:(id)sender {
    NSInteger row = self.sessionTableView.selectedRow;
    if (row < 0 || row >= (NSInteger)self.recentSessions.count) return;

    NSDictionary *session = self.recentSessions[(NSUInteger)row];
    [self openConnectionHost:session[@"host"] port:[session[@"port"] intValue] username:session[@"username"]];
}

- (void)deleteSelectedSessions:(id)sender {
    NSIndexSet *selectedRows = self.sessionTableView.selectedRowIndexes;
    if (selectedRows.count == 0) return;

    [self.recentSessions removeObjectsAtIndexes:selectedRows];
    [self saveRecentSessions];
    [self rebuildRecentMenu];
    [self.sessionTableView reloadData];
}

- (NSInteger)numberOfRowsInTableView:(NSTableView *)tableView {
    if (tableView == self.activeSessionsTableView) return (NSInteger)self.tabs.count;
    if (tableView == self.logTableView) return (NSInteger)self.logFileURLs.count;
    return (NSInteger)self.recentSessions.count;
}

- (id)tableView:(NSTableView *)tableView objectValueForTableColumn:(NSTableColumn *)tableColumn row:(NSInteger)row {
    if (tableView == self.activeSessionsTableView) {
        if (row < 0 || row >= (NSInteger)self.tabs.count) return @"";

        TerminalTab *tab = self.tabs[(NSUInteger)row];
        NSString *identifier = tableColumn.identifier;
        if ([identifier isEqualToString:@"status"]) return tab.session.running ? @"Connected" : @"Disconnected";
        if ([identifier isEqualToString:@"session"]) return [NSString stringWithFormat:@"%@@%@:%d", tab.username ?: @"", tab.host ?: @"", tab.port];
        if ([identifier isEqualToString:@"host"]) return tab.host ?: @"";
        if ([identifier isEqualToString:@"port"]) return [NSString stringWithFormat:@"%d", tab.port];
        if ([identifier isEqualToString:@"tab"]) return tab.item.label ?: @"";
        return @"";
    }

    if (tableView == self.logTableView) {
        if (row < 0 || row >= (NSInteger)self.logFileURLs.count) return @"";

        NSURL *url = self.logFileURLs[(NSUInteger)row];
        NSString *identifier = tableColumn.identifier;
        if ([identifier isEqualToString:@"name"]) return url.lastPathComponent ?: @"";

        NSDictionary *attributes = [NSFileManager.defaultManager attributesOfItemAtPath:url.path error:nil];
        if ([identifier isEqualToString:@"modified"]) {
            NSDate *modified = attributes[NSFileModificationDate];
            if (!modified) return @"";
            NSDateFormatter *formatter = [[NSDateFormatter alloc] init];
            formatter.dateStyle = NSDateFormatterMediumStyle;
            formatter.timeStyle = NSDateFormatterShortStyle;
            return [formatter stringFromDate:modified];
        }
        if ([identifier isEqualToString:@"size"]) {
            unsigned long long size = [attributes[NSFileSize] unsignedLongLongValue];
            return [NSByteCountFormatter stringFromByteCount:(long long)size countStyle:NSByteCountFormatterCountStyleFile];
        }
        return @"";
    }

    if (row < 0 || row >= (NSInteger)self.recentSessions.count) return @"";

    NSDictionary *session = self.recentSessions[(NSUInteger)row];
    NSString *identifier = tableColumn.identifier;
    if ([identifier isEqualToString:@"lastUsed"]) {
        NSTimeInterval timestamp = [session[@"lastUsed"] doubleValue];
        if (timestamp <= 0) return @"";

        NSDateFormatter *formatter = [[NSDateFormatter alloc] init];
        formatter.dateStyle = NSDateFormatterMediumStyle;
        formatter.timeStyle = NSDateFormatterShortStyle;
        return [formatter stringFromDate:[NSDate dateWithTimeIntervalSince1970:timestamp]];
    }

    id value = session[identifier];
    return value ? [NSString stringWithFormat:@"%@", value] : @"";
}

- (NSURL *)logDirectoryURLCreateIfNeeded:(BOOL)createIfNeeded {
    NSURL *homeURL = NSFileManager.defaultManager.homeDirectoryForCurrentUser;
    NSURL *directoryURL = [homeURL URLByAppendingPathComponent:@"MacSshSessions" isDirectory:YES];
    if (createIfNeeded) {
        [NSFileManager.defaultManager createDirectoryAtURL:directoryURL withIntermediateDirectories:YES attributes:nil error:nil];
    }
    return directoryURL;
}

- (NSURL *)logFileURLForHost:(NSString *)host username:(NSString *)username startDate:(NSDate *)startDate {
    NSURL *directoryURL = [self logDirectoryURLCreateIfNeeded:YES];

    NSDateFormatter *formatter = [[NSDateFormatter alloc] init];
    formatter.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"];
    formatter.dateFormat = @"yyyy-MM-dd-HH-mm-ss";

    NSString *safeUsername = [self safeLogFileComponent:username.length ? username : NSUserName()];
    NSString *safeHost = [self safeLogFileComponent:host.length ? host : @"unknown-host"];
    NSString *filename = [NSString stringWithFormat:@"%@-%@-%@.log", safeUsername, safeHost, [formatter stringFromDate:startDate]];
    return [directoryURL URLByAppendingPathComponent:filename];
}

- (NSString *)safeLogFileComponent:(NSString *)component {
    NSMutableCharacterSet *allowed = [NSMutableCharacterSet alphanumericCharacterSet];
    [allowed addCharactersInString:@".-_"];

    NSMutableString *safe = [NSMutableString string];
    for (NSUInteger i = 0; i < component.length; i++) {
        unichar ch = [component characterAtIndex:i];
        [safe appendString:[allowed characterIsMember:ch] ? [NSString stringWithFormat:@"%C", ch] : @"_"];
    }
    return safe.length > 0 ? safe : @"unknown";
}

- (NSFileHandle *)createLogFileAtURL:(NSURL *)url {
    if (!url) return nil;

    [NSFileManager.defaultManager createFileAtPath:url.path contents:nil attributes:nil];
    return [NSFileHandle fileHandleForWritingToURL:url error:nil];
}

- (void)refreshLogFiles {
    NSURL *directoryURL = [self logDirectoryURLCreateIfNeeded:NO];
    NSArray<NSURL *> *files = [NSFileManager.defaultManager contentsOfDirectoryAtURL:directoryURL includingPropertiesForKeys:@[NSURLContentModificationDateKey] options:0 error:nil] ?: @[];
    NSPredicate *logPredicate = [NSPredicate predicateWithBlock:^BOOL(NSURL *url, NSDictionary *bindings) {
        return [url.pathExtension.lowercaseString isEqualToString:@"log"];
    }];
    NSArray<NSURL *> *logFiles = [files filteredArrayUsingPredicate:logPredicate];
    NSArray<NSURL *> *sortedFiles = [logFiles sortedArrayUsingComparator:^NSComparisonResult(NSURL *a, NSURL *b) {
        NSDate *aDate = nil;
        NSDate *bDate = nil;
        [a getResourceValue:&aDate forKey:NSURLContentModificationDateKey error:nil];
        [b getResourceValue:&bDate forKey:NSURLContentModificationDateKey error:nil];
        return [bDate ?: NSDate.distantPast compare:aDate ?: NSDate.distantPast];
    }];
    self.logFileURLs = [sortedFiles mutableCopy];
    [self.logTableView reloadData];
}

- (void)deleteAllLogFiles {
    NSURL *directoryURL = [self logDirectoryURLCreateIfNeeded:NO];
    NSArray<NSURL *> *files = [NSFileManager.defaultManager contentsOfDirectoryAtURL:directoryURL includingPropertiesForKeys:nil options:0 error:nil] ?: @[];
    for (NSURL *url in files) {
        if ([url.pathExtension.lowercaseString isEqualToString:@"log"]) {
            [NSFileManager.defaultManager removeItemAtURL:url error:nil];
        }
    }
    [self refreshLogFiles];
}

- (void)applicationWillTerminate:(NSNotification *)notification {
    for (TerminalTab *tab in self.tabs) {
        [tab closeLogFile];
    }
    if ([NSUserDefaults.standardUserDefaults boolForKey:ClearLogsOnExitKey]) {
        [self deleteAllLogFiles];
    }
}

- (void)showConnectionDialogWithSession:(NSDictionary *)session {
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = @"New SSH Connection";
    alert.informativeText = @"Enter host, port, and username. Password entry happens inside the SSH terminal and is not saved.";
    [alert addButtonWithTitle:@"Connect"];
    [alert addButtonWithTitle:@"Cancel"];

    NSView *view = [[NSView alloc] initWithFrame:NSMakeRect(0,0,380,110)];
    NSTextField *(^label)(NSString *, CGFloat) = ^NSTextField *(NSString *text, CGFloat y) {
        NSTextField *l = [NSTextField labelWithString:text];
        l.frame = NSMakeRect(0,y,90,24);
        return l;
    };
    NSTextField *host = [[NSTextField alloc] initWithFrame:NSMakeRect(100,80,260,24)];
    NSTextField *port = [[NSTextField alloc] initWithFrame:NSMakeRect(100,45,80,24)];
    NSTextField *user = [[NSTextField alloc] initWithFrame:NSMakeRect(100,10,260,24)];
    host.stringValue = session[@"host"] ?: @"";
    port.stringValue = [NSString stringWithFormat:@"%@", session[@"port"] ?: @22];
    user.stringValue = session[@"username"] ?: NSUserName();
    [view addSubview:label(@"Host:",80)]; [view addSubview:host];
    [view addSubview:label(@"Port:",45)]; [view addSubview:port];
    [view addSubview:label(@"Username:",10)]; [view addSubview:user];
    alert.accessoryView = view;

    [alert beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse returnCode) {
        if (returnCode == NSAlertFirstButtonReturn && host.stringValue.length > 0) {
            [self openConnectionHost:host.stringValue port:port.intValue ?: 22 username:user.stringValue];
        }
    }];
}

- (void)openConnectionHost:(NSString *)host port:(int)port username:(NSString *)username {
    [self createMainWindow];
    if (self.tabView.numberOfTabViewItems == 1 && [self.tabView.tabViewItems.firstObject.identifier isEqual:@"welcome"]) {
        [self.tabView removeTabViewItem:self.tabView.tabViewItems.firstObject];
    }
    PtySession *session = [[PtySession alloc] initWithHost:host port:port username:username];
    TerminalView *terminal = [[TerminalView alloc] initWithFrame:self.tabView.bounds];
    terminal.font = [NSFont monospacedSystemFontOfSize:13 weight:NSFontWeightRegular];
    terminal.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    terminal.editable = NO;
    terminal.selectable = YES;
    terminal.automaticQuoteSubstitutionEnabled = NO;
    terminal.automaticDashSubstitutionEnabled = NO;
    terminal.automaticTextReplacementEnabled = NO;
    terminal.session = session;
    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:self.tabView.bounds];
    scroll.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    scroll.hasVerticalScroller = YES;
    terminal.frame = scroll.contentView.bounds;
    scroll.documentView = terminal;

    NSTabViewItem *item = [[NSTabViewItem alloc] initWithIdentifier:session.displayName];
    item.label = session.displayName;
    item.view = scroll;

    TerminalTab *tab = [[TerminalTab alloc] init];
    tab.session = session;
    tab.terminal = terminal;
    tab.item = item;
    tab.host = host;
    tab.username = username;
    tab.port = port;
    terminal.tab = tab;
    tab.logFileURL = [self logFileURLForHost:host username:username startDate:[NSDate date]];
    tab.logFileHandle = [self createLogFileAtURL:tab.logFileURL];
    session.delegate = tab;
    [terminal updateTerminalSizeFromBounds:YES];
    [self.tabs addObject:tab];
    [self.activeSessionsTableView reloadData];
    [self.tabView addTabViewItem:item];
    [self.tabView selectTabViewItem:item];
    [self.window makeFirstResponder:terminal];

    NSError *error = nil;
    if (![session startWithError:&error]) {
        [tab appendText:[NSString stringWithFormat:@"Failed to start ssh: %@\n", error.localizedDescription]];
        [tab closeLogFile];
    } else {
        [tab appendText:[NSString stringWithFormat:@"Connecting to %@...\n", session.displayName]];
        [self addRecentHost:host port:port username:username];
    }
}

- (TerminalTab *)currentTab {
    NSTabViewItem *item = self.tabView.selectedTabViewItem;
    for (TerminalTab *tab in self.tabs) if (tab.item == item) return tab;
    return nil;
}

- (void)disconnectCurrent:(id)sender {
    [[self currentTab].session close];
    [self.activeSessionsTableView reloadData];
}
- (void)reconnectCurrent:(id)sender {
    TerminalTab *tab = [self currentTab];
    if (!tab) return;
    [self reconnectTab:tab];
}

- (void)reconnectTab:(TerminalTab *)tab {
    if (!tab) return;

    [tab.session close];
    [tab closeLogFile];
    tab.autoPasswordSubmittedForSession = NO;
    tab.expectingPassword = NO;
    tab.pendingPassword = nil;

    PtySession *session = [[PtySession alloc] initWithHost:tab.host port:tab.port username:tab.username];
    tab.session = session;
    tab.terminal.session = session;
    session.delegate = tab;
    tab.item.identifier = session.displayName;
    tab.item.label = session.displayName;
    tab.logFileURL = [self logFileURLForHost:tab.host username:tab.username startDate:[NSDate date]];
    tab.logFileHandle = [self createLogFileAtURL:tab.logFileURL];
    [tab.terminal resetTerminalState];
    [tab.terminal updateTerminalSizeFromBounds:YES];

    NSError *error = nil;
    if (![session startWithError:&error]) {
        [tab appendText:[NSString stringWithFormat:@"Failed to reconnect ssh: %@\n", error.localizedDescription]];
        [tab closeLogFile];
    } else {
        [tab appendText:[NSString stringWithFormat:@"Reconnecting to %@...\n", session.displayName]];
        [self addRecentHost:tab.host port:tab.port username:tab.username];
    }
    [self.activeSessionsTableView reloadData];
}
- (void)closeCurrentTab:(id)sender {
    TerminalTab *tab = [self currentTab];
    if (!tab) return;
    [tab.session close];
    [tab closeLogFile];
    [self.tabView removeTabViewItem:tab.item];
    [self.tabs removeObject:tab];
    [self.activeSessionsTableView reloadData];
    if (self.tabView.numberOfTabViewItems == 0) [self showWelcomeTabIfNeeded];
}

- (void)increaseFont:(id)sender { [self changeFontBy:1.0]; }
- (void)decreaseFont:(id)sender { [self changeFontBy:-1.0]; }
- (void)changeFontBy:(CGFloat)delta {
    TerminalView *t = [self currentTab].terminal;
    if (!t) return;
    CGFloat size = MAX(8, MIN(36, t.font.pointSize + delta));
    t.font = [NSFont monospacedSystemFontOfSize:size weight:NSFontWeightRegular];
    [t updateTerminalSizeFromBounds:YES];
}

- (void)loadRecentSessions {
    NSArray *arr = [NSUserDefaults.standardUserDefaults arrayForKey:RecentSessionsKey];
    self.recentSessions = arr ? [arr mutableCopy] : [NSMutableArray array];
}
- (void)saveRecentSessions {
    [NSUserDefaults.standardUserDefaults setObject:self.recentSessions forKey:RecentSessionsKey];
}
- (void)addRecentHost:(NSString *)host port:(int)port username:(NSString *)username {
    NSDictionary *entry = @{ @"host": host ?: @"", @"port": @(port), @"username": username ?: @"", @"lastUsed": @([[NSDate date] timeIntervalSince1970]) };
    NSIndexSet *matches = [self.recentSessions indexesOfObjectsPassingTest:^BOOL(NSDictionary *obj, NSUInteger idx, BOOL *stop) {
        return [obj[@"host"] isEqual:host] && [obj[@"username"] isEqual:username] && [obj[@"port"] intValue] == port;
    }];
    if (matches.count) [self.recentSessions removeObjectsAtIndexes:matches];
    [self.recentSessions insertObject:entry atIndex:0];
    while (self.recentSessions.count > MaxRecentSessions) [self.recentSessions removeLastObject];
    [self saveRecentSessions];
    [self rebuildRecentMenu];
    [self.sessionTableView reloadData];
}
- (void)rebuildRecentMenu {
    [self.recentMenu removeAllItems];
    NSMenuItem *activeSessions = [self.recentMenu addItemWithTitle:@"Show Active Sessions" action:@selector(showActiveSessions:) keyEquivalent:@""];
    activeSessions.target = self;
    NSMenuItem *manager = [self.recentMenu addItemWithTitle:@"Manage Sessions..." action:@selector(showSessionManager:) keyEquivalent:@""];
    manager.target = self;
    [self.recentMenu addItem:[NSMenuItem separatorItem]];

    if (self.recentSessions.count == 0) {
        NSMenuItem *empty = [self.recentMenu addItemWithTitle:@"No Recent Sessions" action:nil keyEquivalent:@""];
        empty.enabled = NO;
    } else {
        for (NSUInteger i = 0; i < self.recentSessions.count; i++) {
            NSDictionary *s = self.recentSessions[i];
            NSString *title = [NSString stringWithFormat:@"%@@%@:%@", s[@"username"] ?: @"", s[@"host"] ?: @"", s[@"port"] ?: @22];
            NSMenuItem *mi = [self.recentMenu addItemWithTitle:title action:@selector(openRecent:) keyEquivalent:@""];
            mi.target = self;
            mi.tag = (NSInteger)i;
        }
        [self.recentMenu addItem:[NSMenuItem separatorItem]];
        [self.recentMenu addItemWithTitle:@"Clear Recent Sessions" action:@selector(clearRecent:) keyEquivalent:@""].target = self;
    }
}
- (void)openRecent:(NSMenuItem *)sender {
    if (sender.tag >= 0 && sender.tag < (NSInteger)self.recentSessions.count) {
        NSDictionary *s = self.recentSessions[(NSUInteger)sender.tag];
        [self openConnectionHost:s[@"host"] port:[s[@"port"] intValue] username:s[@"username"]];
    }
}
- (void)clearRecent:(id)sender {
    [self.recentSessions removeAllObjects];
    [self saveRecentSessions];
    [self rebuildRecentMenu];
    [self.sessionTableView reloadData];
}
@end
