"""Builds eval/datasets/tool-routing (CON-346): tasks an agent can only finish by running one named Connapse tool.

The routing set checks that an agent given the compact tool list (four search tools plus find_tools and the three
call tools) still reaches the tool a task needs, compared with an agent shown every tool. Each task names a gold tool;
it succeeds when that tool runs without an error within the call cap, called directly or through a call tool.

Every task is written here by hand and the output is deterministic: run it again and the files come out byte for byte
the same. `{u}` in a task is replaced at run time by a fresh id, so a task that creates, changes or deletes something
works on its own objects. `setup` calls run on the full tool list as the eval admin before the agent starts.

Usage: python eval/tools/build_tool_routing.py   (writes eval/datasets/tool-routing/{fixtures.json,tasks.jsonl})
"""
import json
import pathlib

OUT = pathlib.Path(__file__).resolve().parents[1] / "datasets" / "tool-routing"
DOCS = "eval-routing-docs"
CODE = "eval-routing-code"
SPACE = "routing-memory"

FIXTURE_DOCS = {
    "/policies/travel.md": "# Travel policy\n\nBook flights at least 14 days ahead. Economy class for trips under 6 hours. "
                           "Hotels up to $220 a night in tier-1 cities. Submit receipts within 30 days.\n",
    "/policies/expenses.md": "# Expense policy\n\nMeals are reimbursed up to $75 a day. Client dinners need a manager's "
                             "approval in advance. Mileage is paid at the federal rate.\n",
    "/handbook/onboarding.md": "# Onboarding\n\nNew hires get a laptop on day one, meet their buddy in week one and "
                               "finish security training within 10 days.\n",
    "/handbook/benefits.md": "# Benefits\n\nHealth cover starts on the first day. The 401(k) match is 4%. Parental leave "
                             "is 16 weeks.\n",
    "/engineering/deploy.md": "# Deploying\n\nDeploys go out Tuesday and Thursday. Run the smoke suite before promoting a "
                              "build. Rollbacks use the previous image tag.\n",
    "/engineering/oncall.md": "# On-call\n\nThe on-call rotation is weekly and hands over on Monday at 10:00. Page the "
                              "secondary after 15 minutes without an acknowledgement.\n",
    "/notes/2026-11-02.md": "# Notes 2026-11-02\n\nDecided to move the billing service to the new cluster in December.\n",
    "/notes/2026-11-05.md": "# Notes 2026-11-05\n\nThe Orion migration slipped a week because of the vendor contract.\n",
}

FIXTURE_CODE = {
    "billing/ledger.py": '''"""The general ledger."""


class Ledger:
    def __init__(self):
        self.entries = []

    def post(self, account, amount):
        """Record an amount against an account."""
        self.entries.append((account, amount))

    def balance(self, account):
        return sum(a for acc, a in self.entries if acc == account)
''',
    "billing/tax.py": '''"""Sales tax."""

RATES = {"TX": 0.0625, "CA": 0.0725, "NY": 0.04}


def compute_tax(state, amount):
    return round(amount * RATES.get(state, 0.0), 2)
''',
    "billing/invoice.py": '''"""Invoices."""
from billing.ledger import Ledger
from billing.tax import compute_tax


class Invoice:
    def __init__(self, customer, state, lines):
        self.customer = customer
        self.state = state
        self.lines = lines

    def total(self):
        subtotal = sum(price * qty for price, qty in self.lines)
        return subtotal + compute_tax(self.state, subtotal)

    def issue(self, ledger: Ledger):
        ledger.post("receivables:" + self.customer, self.total())
        return self.total()
''',
    "billing/refunds.py": '''"""Refunds."""
from billing.ledger import Ledger


def refund(ledger: Ledger, customer, amount):
    ledger.post("receivables:" + customer, -amount)
''',
    "api/handlers.py": '''"""HTTP handlers."""
from billing.invoice import Invoice
from billing.ledger import Ledger

LEDGER = Ledger()


def create_invoice(request):
    invoice = Invoice(request["customer"], request["state"], request["lines"])
    return {"total": invoice.issue(LEDGER)}


def customer_balance(request):
    return {"balance": LEDGER.balance("receivables:" + request["customer"])}
''',
    "tests/test_invoice.py": '''from billing.invoice import Invoice
from billing.ledger import Ledger


def test_issue_posts_total():
    ledger = Ledger()
    total = Invoice("acme", "TX", [(100.0, 2)]).issue(ledger)
    assert ledger.balance("receivables:acme") == total
''',
    "Shop/Services/IOrderService.cs": '''namespace Shop.Services;

public interface IOrderService
{
    Order PlaceOrder(Cart cart);
}
''',
    "Shop/Services/OrderService.cs": '''namespace Shop.Services;

public sealed class OrderService(PaymentGateway payments, InventoryClient inventory) : IOrderService
{
    public Order PlaceOrder(Cart cart)
    {
        inventory.Reserve(cart);
        string receipt = payments.Charge(cart.CustomerId, cart.Total);
        return new Order(cart.CustomerId, cart.Total, receipt);
    }
}
''',
    "Shop/Services/PaymentGateway.cs": '''namespace Shop.Services;

public class PaymentGateway
{
    public virtual string Charge(string customerId, decimal amount) => $"rcpt-{customerId}-{amount}";
}
''',
    "Shop/Services/InventoryClient.cs": '''namespace Shop.Services;

public class InventoryClient
{
    public void Reserve(Cart cart)
    {
    }
}
''',
    "Shop/Controllers/OrdersController.cs": '''namespace Shop.Controllers;

using Shop.Services;

public sealed class OrdersController(IOrderService orders)
{
    public Order Post(Cart cart) => orders.PlaceOrder(cart);
}
''',
    "Shop/Models/Order.cs": '''namespace Shop;

public sealed record Order(string CustomerId, decimal Total, string Receipt);

public sealed record Cart(string CustomerId, decimal Total);
''',
}

# Facts the shared memory space starts with: (subject, attribute, value, validFrom, validTo).
FIXTURE_FACTS = [
    ("Halcyon", "database", "MySQL 8", "2025-01-10", "2026-03-01"),
    ("Halcyon", "database", "PostgreSQL 16", "2026-03-01", None),
    ("Halcyon", "tech lead", "Dana Reyes", "2025-06-01", None),
    ("Orion", "region", "us-east-2", "2026-02-15", None),
    ("Orion", "owner", "Platform team", "2026-01-05", None),
    ("billing service", "cluster", "prod-west", "2025-09-01", None),
    ("Kestrel", "release cadence", "every two weeks", "2026-04-01", None),
]


def upload(path, text):
    folder, name = path.rsplit("/", 1)
    return {"tool": "upload_file", "arguments": {"containerId": DOCS, "textContent": text, "fileName": name, "path": folder + "/"}}


def remember(subject, attribute, value, valid_from=None):
    args = {"subject": subject, "attribute": attribute, "value": value, "space": SPACE}
    if valid_from:
        args["validFrom"] = valid_from
    return {"tool": "memory_remember", "arguments": args}


def task(group, gold, prompt, setup=None, expect=None):
    return {"group": group, "gold": gold, "prompt": prompt, "setup": setup or [], "expect": expect or {}}


DOCUMENT = [
    task("document", "container_create", "Create a new container called proj-{u} for the Q1 planning documents.", expect={"name": "proj-{u}"}),
    task("document", "container_create", "Set up a fresh knowledge container named legal-{u}, described as contracts and NDAs.", expect={"name": "legal-{u}"}),
    task("document", "container_create", "We need a separate place to keep the design reviews. Make a container named design-{u}.", expect={"name": "design-{u}"}),
    task("document", "container_delete", "The container scratch-{u} is no longer needed. Delete it.",
         setup=[{"tool": "container_create", "arguments": {"name": "scratch-{u}"}}], expect={"containerId": "scratch-{u}"}),
    task("document", "container_delete", "Remove the whole container old-{u} and everything in it.",
         setup=[{"tool": "container_create", "arguments": {"name": "old-{u}"}}], expect={"containerId": "old-{u}"}),
    task("document", "container_delete", "Tear down the temporary container tmp-{u}; the experiment is over.",
         setup=[{"tool": "container_create", "arguments": {"name": "tmp-{u}"}}], expect={"containerId": "tmp-{u}"}),
    task("document", "upload_file", f"Save a new note in the {DOCS} container at /scratch/{{u}}/ named standup-{{u}}.md "
         "with this text: 'Standup: the deploy moved to Thursday.'", expect={"fileName": "standup-{u}"}),
    task("document", "upload_file", f"Add a document called faq-{{u}}.md to the {DOCS} container under /scratch/{{u}}/. "
         "Its content is: 'Q: Who approves client dinners? A: Your manager, in advance.'", expect={"fileName": "faq-{u}"}),
    task("document", "bulk_upload", f"Upload these three files to the {DOCS} container in folder /scratch/{{u}}/ in a single "
         "call: a-{u}.md with 'alpha', b-{u}.md with 'beta', c-{u}.md with 'gamma'.", expect={"files": "a-{u}"}),
    task("document", "bulk_upload", f"In one request, add two text files to the {DOCS} container under /scratch/{{u}}/: "
         "minutes-{u}.txt containing 'Minutes: none' and agenda-{u}.txt containing 'Agenda: budget'.", expect={"files": "minutes-{u}"}),
    task("document", "append_to_file", f"Add the line '- 16:00 vendor call moved to Friday' to the end of the existing file "
         f"/scratch/{{u}}/log-{{u}}.md in the {DOCS} container. Keep what is already there.",
         setup=[upload("/scratch/{u}/log-{u}.md", "# Log\n\n- 09:00 standup\n")], expect={"file": "log-{u}"}),
    task("document", "append_to_file", f"Append 'Decision: ship on the 14th.' to the bottom of /scratch/{{u}}/decisions-{{u}}.md "
         f"in the {DOCS} container without rewriting the file.",
         setup=[upload("/scratch/{u}/decisions-{u}.md", "# Decisions\n\n- Use PostgreSQL.\n")], expect={"file": "decisions-{u}"}),
    task("document", "append_to_file", f"The running journal /scratch/{{u}}/journal-{{u}}.md in the {DOCS} container needs a new "
         "entry at the end: '2026-11-12: wrapped up the routing eval.'",
         setup=[upload("/scratch/{u}/journal-{u}.md", "# Journal\n\n2026-11-11: started.\n")], expect={"file": "journal-{u}"}),
    task("document", "delete_file", f"Delete the file /scratch/{{u}}/obsolete-{{u}}.md from the {DOCS} container.",
         setup=[upload("/scratch/{u}/obsolete-{u}.md", "# Obsolete\n\nThis page is out of date.\n")]),
    task("document", "delete_file", f"Someone uploaded /scratch/{{u}}/secret-{{u}}.md to the {DOCS} container by mistake. Remove that one document.",
         setup=[upload("/scratch/{u}/secret-{u}.md", "# Do not share\n\nPlaceholder.\n")]),
    task("document", "delete_file", f"Get rid of the draft /scratch/{{u}}/draft-{{u}}.md in the {DOCS} container; the final version replaced it.",
         setup=[upload("/scratch/{u}/draft-{u}.md", "# Draft\n\nRough notes.\n")]),
    task("document", "bulk_delete", f"Delete all three files in /scratch/{{u}}/ in the {DOCS} container with one bulk delete.",
         setup=[upload("/scratch/{u}/x-{u}.md", "x"), upload("/scratch/{u}/y-{u}.md", "y"), upload("/scratch/{u}/z-{u}.md", "z")]),
    task("document", "bulk_delete", f"Clean out the folder /scratch/{{u}}/ in the {DOCS} container: remove both of its documents in a single call.",
         setup=[upload("/scratch/{u}/one-{u}.md", "one"), upload("/scratch/{u}/two-{u}.md", "two")]),
    task("document", "container_stats", f"How much storage does the {DOCS} container use, and which embedding model indexed it?"),
    task("document", "container_stats", f"Give me the document count, chunk count and storage size of the {DOCS} container."),
]

CODE_TASKS = [
    task("code", "code_find_symbol", f"In the {CODE} container, where is compute_tax defined?", expect={"query": "compute_tax"}),
    task("code", "code_find_symbol", f"Find the definition of the PaymentGateway class in the indexed code of {CODE}.", expect={"query": "PaymentGateway"}),
    task("code", "code_find_symbol", f"Which file in {CODE} defines the Ledger.balance method?", expect={"query": "balance"}),
    task("code", "code_find_symbol", f"Locate where the OrderService class is declared in the {CODE} code.", expect={"query": "OrderService"}),
    task("code", "code_find_symbol", f"Show me where the function refund is defined in {CODE}.", expect={"query": "refund"}),
    task("code", "code_references", f"Which files call Ledger.post in the {CODE} code?", expect={"symbol": "post"}),
    task("code", "code_references", f"List every place in {CODE} that uses the Invoice class.", expect={"symbol": "Invoice"}),
    task("code", "code_references", f"What implements or inherits IOrderService in the {CODE} code?", expect={"symbol": "IOrderService"}),
    task("code", "code_references", f"Find the callers of compute_tax in {CODE}.", expect={"symbol": "compute_tax"}),
    task("code", "code_references", f"Who calls PaymentGateway.Charge in the {CODE} code?", expect={"symbol": "Charge"}),
    task("code", "code_impact", f"If I change the signature of Ledger.post in {CODE}, what functions and tests could break, including indirect callers?", expect={"symbol": "post"}),
    task("code", "code_impact", f"What is the blast radius of changing compute_tax in {CODE}, two hops out?", expect={"symbol": "compute_tax"}),
    task("code", "code_impact", f"Before I refactor OrderService.PlaceOrder in {CODE}, show everything that depends on it directly or transitively.", expect={"symbol": "PlaceOrder"}),
    task("code", "code_impact", f"Which tests and callers would a change to Invoice.issue in {CODE} affect, through other functions too?", expect={"symbol": "issue"}),
    task("code", "code_impact", f"Assess the impact of renaming PaymentGateway.Charge in {CODE}: what calls it, directly and indirectly?", expect={"symbol": "Charge"}),
    task("code", "code_repo_map", f"I'm new to the {CODE} repository. Give me a map of its most important files and their main definitions."),
    task("code", "code_repo_map", f"Give me an overview of the code structure under the billing folder of {CODE}: the key files and what they define.", expect={"path": "billing"}),
    task("code", "code_repo_map", f"Which files in {CODE} does the rest of the code depend on most?"),
    task("code", "code_repo_map", f"Map the Shop folder of the {CODE} code: its central files and their definitions.", expect={"path": "Shop"}),
    task("code", "code_repo_map", f"Orient me in the {CODE} codebase: what are the hub files and main classes?"),
]

MEMORY = [
    task("memory", "memory_spaces", "Which memory spaces can I use, and can I add facts to each?"),
    task("memory", "memory_spaces", "List the team memory spaces available to me.", expect={"kind": "team"}),
    task("memory", "memory_space_create", "Create a personal memory space named notes-{u} for my own working notes.", expect={"name": "notes-{u}"}),
    task("memory", "memory_space_create", "Make a new team memory space called team-{u} for facts about the payments squad.", expect={"name": "team-{u}"}),
    task("memory", "memory_space_create", "Start a fresh memory space, personal, named scratch-mem-{u}.", expect={"name": "scratch-mem-{u}"}),
    task("memory", "memory_remember", f"Remember in the {SPACE} memory space that project Falcon-{{u}}'s database is DynamoDB.", expect={"subject": "Falcon-{u}"}),
    task("memory", "memory_remember", f"Store this fact in the {SPACE} memory space: the tech lead of Atlas-{{u}} is Priya Nair, as of 2026-10-01.", expect={"subject": "Atlas-{u}"}),
    task("memory", "memory_remember", f"Note in memory space {SPACE} that service Pulse-{{u}} runs in region eu-west-1.", expect={"subject": "Pulse-{u}"}),
    task("memory", "memory_remember", f"Save to the {SPACE} memory space: Juniper-{{u}} releases every Friday.", expect={"subject": "Juniper-{u}"}),
    task("memory", "memory_recall", f"According to memory (space {SPACE}), what database does Halcyon use right now?", expect={"query|subject": "Halcyon"}),
    task("memory", "memory_recall", "Check memory: who is the tech lead of Halcyon?", expect={"query|subject": "Halcyon"}),
    task("memory", "memory_recall", "What region is Orion in, according to the facts in memory?", expect={"query|subject": "Orion"}),
    task("memory", "memory_recall", f"Look up in the {SPACE} memory space which cluster the billing service runs on.", expect={"query|subject": "billing"}),
    task("memory", "memory_timeline", "How has Halcyon's database changed over time, according to memory? Give the full history in date order.", expect={"query|subject": "Halcyon"}),
    task("memory", "memory_timeline", f"Show the dated history of every fact about Orion in the {SPACE} memory space.", expect={"query|subject": "Orion"}),
    task("memory", "memory_timeline", "Give me a chronological timeline of what memory recorded about Halcyon during 2025 and 2026.", expect={"query|subject": "Halcyon"}),
    task("memory", "memory_correct", f"In memory, Comet-{{u}}'s owner changed to the Data team on 2026-11-01. Correct the existing fact.",
         setup=[remember("Comet-{u}", "owner", "Web team", "2025-05-01")]),
    task("memory", "memory_correct", f"The memory fact that Vega-{{u}}'s region is us-west-1 was simply wrong; it has always been us-west-2. Fix it.",
         setup=[remember("Vega-{u}", "region", "us-west-1", "2026-01-01")]),
    task("memory", "memory_forget", f"Delete the memory fact about Nova-{{u}}'s pager number; it should never have been stored.",
         setup=[remember("Nova-{u}", "pager number", "555-0100")]),
    task("memory", "memory_forget", f"Forget the fact that Rigel-{{u}}'s codename is Bluebird. Remove it from memory entirely.",
         setup=[remember("Rigel-{u}", "codename", "Bluebird")]),
]

DEV = [
    task("document", "container_create", "Create a container named dev-{u}.", expect={"name": "dev-{u}"}),
    task("document", "delete_file", f"Delete /scratch/{{u}}/dev-{{u}}.md from the {DOCS} container.",
         setup=[upload("/scratch/{u}/dev-{u}.md", "dev")]),
    task("document", "append_to_file", f"Append 'done' to /scratch/{{u}}/todo-{{u}}.md in the {DOCS} container.",
         setup=[upload("/scratch/{u}/todo-{u}.md", "# Todo\n")], expect={"file": "todo-{u}"}),
    task("document", "container_stats", f"What are the statistics of the {DOCS} container?"),
    task("code", "code_find_symbol", f"Where is the Invoice class defined in {CODE}?", expect={"query": "Invoice"}),
    task("code", "code_references", f"Which files use InventoryClient in {CODE}?", expect={"symbol": "InventoryClient"}),
    task("code", "code_impact", f"What would changing Ledger.balance in {CODE} affect, transitively?", expect={"symbol": "balance"}),
    task("code", "code_repo_map", f"Map the api folder of {CODE}.", expect={"path": "api"}),
    task("memory", "memory_recall", "What is Kestrel's release cadence, per memory?", expect={"query|subject": "Kestrel"}),
    task("memory", "memory_remember", f"Remember in {SPACE} that Lyra-{{u}}'s owner is the Mobile team.", expect={"subject": "Lyra-{u}"}),
    task("memory", "memory_timeline", "Show how Orion's facts changed over time.", expect={"query|subject": "Orion"}),
    task("memory", "memory_forget", f"Forget the fact about Dev-{{u}}'s desk number.", setup=[remember("Dev-{u}", "desk number", "4B")]),
]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    rows = []
    for split, tasks, prefix in (("test", DOCUMENT + CODE_TASKS + MEMORY, "t"), ("dev", DEV, "d")):
        for i, t in enumerate(tasks, 1):
            rows.append({"id": f"{prefix}{i:02d}-{t['gold']}", "split": split, **t})
    counts = {}
    for r in rows:
        if r["split"] == "test":
            counts[r["group"]] = counts.get(r["group"], 0) + 1
    assert counts == {"document": 20, "code": 20, "memory": 20}, counts
    assert len({r["id"] for r in rows}) == len(rows)

    fixtures = {
        "docsContainer": DOCS,
        "codeContainer": CODE,
        "memorySpace": SPACE,
        "documents": [{"path": p, "text": t} for p, t in FIXTURE_DOCS.items()],
        "code": [{"path": p, "text": t} for p, t in FIXTURE_CODE.items()],
        "facts": [{"subject": s, "attribute": a, "value": v, "validFrom": f, "validTo": to} for s, a, v, f, to in FIXTURE_FACTS],
    }
    with open(OUT / "fixtures.json", "w", encoding="utf-8", newline="\n") as f:
        json.dump(fixtures, f, indent=2, ensure_ascii=False)
        f.write("\n")
    with open(OUT / "tasks.jsonl", "w", encoding="utf-8", newline="\n") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False, sort_keys=True) + "\n")
    print(f"{len(rows)} tasks ({sum(counts.values())} test, {len(rows) - sum(counts.values())} dev) -> {OUT}")


if __name__ == "__main__":
    main()
