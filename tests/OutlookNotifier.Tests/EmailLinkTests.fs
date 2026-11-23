module OutlookNotifier.Tests.EmailLinkTests

open Xunit
open FsUnit.Xunit
open OutlookNotifier

let private graphWebLink =
    "https://outlook.office365.com/owa/?ItemID=AAMk%2B3AAA%3D&exvsurl=1&viewmodel=ReadMessageItem"

[<Fact>]
let ``forBrowser drops the exvsurl desktop handoff and keeps the rest`` () =
    EmailLink.forBrowser graphWebLink
    |> should equal "https://outlook.office365.com/owa/?ItemID=AAMk%2B3AAA%3D&viewmodel=ReadMessageItem"

[<Fact>]
let ``forBrowser drops exvsurl wherever it appears in the query`` () =
    EmailLink.forBrowser "https://outlook.office.com/owa/?exvsurl=1&ItemID=AAMk"
    |> should equal "https://outlook.office.com/owa/?ItemID=AAMk"

[<Fact>]
let ``forBrowser leaves a link without exvsurl unchanged`` () =
    let link = "https://outlook.office.com/owa/?ItemID=AAMk&viewmodel=ReadMessageItem"
    EmailLink.forBrowser link |> should equal link

[<Fact>]
let ``forBrowser leaves a non-url unchanged`` () =
    EmailLink.forBrowser "not a url" |> should equal "not a url"

[<Fact>]
let ``launchFor substitutes the url into a Chrome-style command`` () =
    EmailLink.launchFor "\"C:\\Chrome\\chrome.exe\" --single-argument %1" "https://x/?a=1&b=2"
    |> should equal (Some("C:\\Chrome\\chrome.exe", "--single-argument https://x/?a=1&b=2"))

[<Fact>]
let ``launchFor keeps the command's own quotes around the url`` () =
    EmailLink.launchFor "\"C:\\Firefox\\firefox.exe\" -osint -url \"%1\"" "https://x/"
    |> should equal (Some("C:\\Firefox\\firefox.exe", "-osint -url \"https://x/\""))

[<Fact>]
let ``launchFor appends the url when the command has no placeholder`` () =
    EmailLink.launchFor "\"C:\\B\\browser.exe\"" "https://x/"
    |> should equal (Some("C:\\B\\browser.exe", "\"https://x/\""))

[<Fact>]
let ``launchFor handles an unquoted executable path`` () =
    EmailLink.launchFor "C:\\B\\browser.exe %1" "https://x/"
    |> should equal (Some("C:\\B\\browser.exe", "https://x/"))

[<Fact>]
let ``launchFor rejects an empty command`` () =
    EmailLink.launchFor "  " "https://x/" |> should equal None
