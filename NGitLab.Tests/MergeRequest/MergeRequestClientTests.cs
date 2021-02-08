using System;
using System.Linq;
using System.Threading.Tasks;
using NGitLab.Models;
using NGitLab.Tests.Docker;
using NUnit.Framework;

namespace NGitLab.Tests
{
    public class MergeRequestClientTests
    {
        [Test]
        public async Task Test_merge_request_api()
        {
            using var context = await GitLabTestContext.CreateAsync();
            var (project, mergeRequest) = context.CreateMergeRequest();
            var mergeRequestClient = context.Client.GetMergeRequest(project.Id);

            Assert.AreEqual(mergeRequest.Id, mergeRequestClient[mergeRequest.Iid].Id, "Test we can get a merge request by IId");

            ListMergeRequest(mergeRequestClient, mergeRequest);
            mergeRequest = UpdateMergeRequest(mergeRequestClient, mergeRequest);
            Test_can_update_a_subset_of_merge_request_fields(mergeRequestClient, mergeRequest);

            await GitLabTestContext.RetryUntilAsync(() => mergeRequestClient[mergeRequest.Iid], mr => string.Equals(mr.MergeStatus, "can_be_merged", StringComparison.Ordinal), TimeSpan.FromSeconds(120));
            AcceptMergeRequest(mergeRequestClient, mergeRequest);
            Assert.IsNull(context.Client.GetRepository(project.Id).Branches.All.FirstOrDefault(x => string.Equals(x.Name, mergeRequest.SourceBranch, StringComparison.Ordinal)));
            var commits = mergeRequestClient.Commits(mergeRequest.Iid).All;
            Assert.IsTrue(commits.Any(), "Can return the commits");
        }

        [Test]
        public async Task Test_merge_request_rebase()
        {
            using var context = await GitLabTestContext.CreateAsync();
            var (project, mergeRequest) = context.CreateMergeRequest();
            var mergeRequestClient = context.Client.GetMergeRequest(project.Id);

            RebaseMergeRequest(mergeRequestClient, mergeRequest);
            var commits = mergeRequestClient.Commits(mergeRequest.Iid).All;
            Assert.IsTrue(commits.Any(), "Can return the commits");
        }

        [Test]
        public async Task Test_gitlab_returns_an_error_when_trying_to_create_a_request_with_same_source_and_destination()
        {
            using var context = await GitLabTestContext.CreateAsync();
            var project = context.CreateProject(initializeWithCommits: true);
            var mergeRequestClient = context.Client.GetMergeRequest(project.Id);

            var exception = Assert.Throws<GitLabException>(() =>
            {
                mergeRequestClient.Create(new MergeRequestCreate
                {
                    Title = "ErrorRequest",
                    SourceBranch = "master",
                    TargetBranch = "master",
                });
            });

            Assert.AreEqual("[\"You can't use same project/branch for source and target\"]", exception.ErrorMessage);
        }

        [Test]
        public async Task Test_merge_request_delete()
        {
            using var context = await GitLabTestContext.CreateAsync();
            var (project, mergeRequest) = context.CreateMergeRequest();
            var mergeRequestClient = context.Client.GetMergeRequest(project.Id);

            mergeRequestClient.Delete(mergeRequest.Iid);

            Assert.Throws<GitLabException>(() =>
            {
                _ = mergeRequestClient[mergeRequest.Iid];
            });
        }

        [Test]
        public async Task Test_merge_request_approvers()
        {
            using var context = await GitLabTestContext.CreateAsync();
            var (project, mergeRequest) = context.CreateMergeRequest();
            var mergeRequestClient = context.Client.GetMergeRequest(project.Id);

            var approvalClient = mergeRequestClient.ApprovalClient(mergeRequest.Iid);
            var approvers = approvalClient.Approvals.Approvers;

            Assert.AreEqual(0, approvers.Length, "Initially no approver defined");

            // --- Add the exampleAdminUser as approver for this MR since adding the MR owners won't increment the number of approvers---
            var userId = context.AdminClient.Users.Current.Id;

            var approversChange = new MergeRequestApproversChange()
            {
                Approvers = new[] { userId },
            };

            approvalClient.ChangeApprovers(approversChange);

            approvers = approvalClient.Approvals.Approvers;

            Assert.AreEqual(1, approvers.Length, "A single approver defined");
            Assert.AreEqual(userId, approvers[0].User.Id, "The approver is the current user");
        }

        [Test]
        public async Task Test_get_unassigned_merge_requests()
        {
            using var context = await GitLabTestContext.CreateAsync();
            var (project, mergeRequest) = context.CreateMergeRequest();
            var mergeRequestClient = context.Client.GetMergeRequest(project.Id);

            var mergeRequests = mergeRequestClient.Get(new MergeRequestQuery { AssigneeId = QueryAssigneeId.None }).ToList();
            Assert.AreEqual(1, mergeRequests.Count, "The query retrieved all open merged requests that are unassigned");

            mergeRequests = mergeRequestClient.Get(new MergeRequestQuery { AssigneeId = context.Client.Users.Current.Id }).ToList();
            Assert.AreEqual(0, mergeRequests.Count, "The query retrieved all open merged requests that are unassigned");
        }

        [Test]
        public async Task Test_get_assigned_merge_requests()
        {
            using var context = await GitLabTestContext.CreateAsync();
            var (project, mergeRequest) = context.CreateMergeRequest();
            var mergeRequestClient = context.Client.GetMergeRequest(project.Id);
            var userId = context.Client.Users.Current.Id;
            mergeRequestClient.Update(mergeRequest.Iid, new MergeRequestUpdate { AssigneeId = userId });

            var mergeRequests = mergeRequestClient.Get(new MergeRequestQuery { AssigneeId = QueryAssigneeId.None }).ToList();
            Assert.AreEqual(0, mergeRequests.Count, "The query retrieved all open merged requests that are unassigned");

            mergeRequests = mergeRequestClient.Get(new MergeRequestQuery { AssigneeId = userId }).ToList();
            Assert.AreEqual(1, mergeRequests.Count, "The query retrieved all open merged requests that are unassigned");
        }

        private void ListMergeRequest(IMergeRequestClient mergeRequestClient, Models.MergeRequest mergeRequest)
        {
            Assert.IsTrue(mergeRequestClient.All.Any(x => x.Id == mergeRequest.Id), "Test 'All' accessor returns the merge request");
            Assert.IsTrue(mergeRequestClient.AllInState(MergeRequestState.opened).Any(x => x.Id == mergeRequest.Id), "Can return all open requests");
            Assert.IsFalse(mergeRequestClient.AllInState(MergeRequestState.merged).Any(x => x.Id == mergeRequest.Id), "Can return all closed requests");
        }

        public static Models.MergeRequest UpdateMergeRequest(IMergeRequestClient mergeRequestClient, Models.MergeRequest request)
        {
            var updatedMergeRequest = mergeRequestClient.Update(request.Iid, new MergeRequestUpdate
            {
                Title = "New title",
                Description = "New description",
                Labels = "a,b",
                SourceBranch = "my-super-feature",
                TargetBranch = "master",
            });

            Assert.AreEqual("New title", updatedMergeRequest.Title);
            Assert.AreEqual("New description", updatedMergeRequest.Description);
            Assert.IsFalse(updatedMergeRequest.MergeWhenPipelineSucceeds);
            CollectionAssert.AreEqual(new[] { "a", "b" }, updatedMergeRequest.Labels);

            return updatedMergeRequest;
        }

        private void Test_can_update_a_subset_of_merge_request_fields(IMergeRequestClient mergeRequestClient, Models.MergeRequest mergeRequest)
        {
            var updated = mergeRequestClient.Update(mergeRequest.Iid, new MergeRequestUpdate
            {
                Title = "Second update",
            });

            Assert.AreEqual("Second update", updated.Title);
            Assert.AreEqual(mergeRequest.Description, updated.Description);
        }

        public static void AcceptMergeRequest(IMergeRequestClient mergeRequestClient, Models.MergeRequest request)
        {
            var mergeRequest = mergeRequestClient.Accept(
                mergeRequestIid: request.Iid,
                message: new MergeRequestMerge
                {
                    MergeCommitMessage = "Merge my-super-feature into master",
                    ShouldRemoveSourceBranch = true,
                    MergeWhenPipelineSucceeds = false,
                    Squash = false,
                });

            Assert.That(mergeRequest.State, Is.EqualTo(nameof(MergeRequestState.merged)));
        }

        public static void RebaseMergeRequest(IMergeRequestClient mergeRequestClient, Models.MergeRequest mergeRequest)
        {
            var rebaseResult = mergeRequestClient.Rebase(mergeRequestIid: mergeRequest.Iid);
            Assert.IsTrue(rebaseResult.RebaseInProgress);
        }
    }
}
